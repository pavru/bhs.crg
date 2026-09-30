using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Состояние счёта: переход «разобран», возврат в черновик и отбор «Разобрать» (C2, issue #1078,
/// ТЗ COST-9).
///
/// <para>Отдельно от <see cref="InvoiceLineTests" />: там набор строк и его отказы, здесь — что счёт
/// меняет состояние и что отбор реестра это состояние показывает. Оснастка общая
/// (<see cref="InvoiceLineTestBase" />), хост и посев — те же.</para>
/// </summary>
// ⚠️ Собрание указывается КАЖДОМУ классу: атрибут не наследуется от базового, а без него класс
// бежит параллельно с чужими хостами — они приводят права ролей при старте и роняют друг друга.
[Collection("Integration")]
public class InvoiceParsedStateTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    [Fact]
    public async Task Строка_без_позиции_живёт_и_счёт_виден_в_отборе_разобрать()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        var view = await LinesAsync(client, invoice, [
            Line(cable, quantity: 10, price: 100m),
            Line(null, quantity: 5, price: 20m, text: "Лоток металлический 100х50 (в справочнике нет)"),
        ]);

        var totals = view.GetProperty("totals");
        Assert.Equal(2, totals.GetProperty("count").GetInt32());
        Assert.Equal(1, totals.GetProperty("withoutNomenclature").GetInt32());
        Assert.Equal("Лоток металлический 100х50 (в справочнике нет)",
            view.GetProperty("lines")[1].GetProperty("supplierText").GetString());

        var queue = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?needsParsing=true");
        var found = queue.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invoice);
        Assert.Equal(1, found.GetProperty("linesWithoutNomenclature").GetInt32());
        Assert.Equal(2, found.GetProperty("linesCount").GetInt32());
    }

    /// <summary>
    /// Счёт, у которого все строки разобраны, в очереди «Разобрать» не стоит — иначе очередь перестала
    /// бы быть очередью и стала бы вторым реестром.
    /// </summary>
    [Fact]
    public async Task Разобранные_строки_из_отбора_уходят()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        await LinesAsync(client, invoice, [Line(null, quantity: 1, price: 1m, text: "ждёт")]);
        var before = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?needsParsing=true");
        Assert.Contains(invoice, before.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));

        await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 1m)]);
        var after = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?needsParsing=true");
        Assert.DoesNotContain(invoice, after.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Разобран_отказывает_пока_строка_ждёт_позиции()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);

        await LinesAsync(client, invoice, [
            Line(cable, quantity: 1, price: 1m),
            Line(null, quantity: 1, price: 1m, text: "ждёт разбора"),
        ]);

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("строка 2 ждёт позиции номенклатуры", text);
        Assert.Contains("Разобрать", text);

        // ⚠️ Проверяется ЦЕЛАЯ фраза, а не подстрока «строка 2». Первая версия сообщения склеивала
        // «ждут строки» с «строка 2» и выдавала «ждут строки строка 2» — подстрока в этой кашице есть,
        // и сторож её пропустил. Нашёл живой прогон, читающий текст глазами человека.
        Assert.DoesNotContain("строки строка", text);
    }

    [Fact]
    public async Task Разобран_отказывает_без_строк()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("строк нет", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Обязательные поля проверяются ЗДЕСЬ, а не при сохранении (ТЗ COST-6.2): черновик без плательщика
    /// живёт, разобранный счёт — нет. Перечень берётся из объявления типа, а не переписан в коде.
    /// </summary>
    [Fact]
    public async Task Разобран_отказывает_без_обязательных_полей()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);

        await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 1m)]);

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("«Плательщик»", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Разобран_проходит_и_состояние_видно_в_реестре()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        await OkAsync(response);

        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Разобран", view.GetProperty("requisites").GetProperty("Состояние").GetString());

        var list = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices");
        var item = list.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invoice);
        Assert.Equal("Разобран", item.GetProperty("state").GetString());
    }

    /// <summary>
    /// Правка строк, оставившая строку без позиции, САМА возвращает счёт в черновик — иначе «разобран»
    /// осталось бы утверждением, перестав быть правдой, и отбор «Разобрать» такой счёт не показал бы.
    /// </summary>
    [Fact]
    public async Task Правка_строк_возвращает_разобранный_счёт_в_черновик()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));

        var view = await LinesAsync(client, invoice, [
            Line(cable, quantity: 1, price: 100m),
            Line(null, quantity: 1, price: 1m, text: "дописали строку, позиции ещё нет"),
        ]);

        Assert.Equal("Черновик", view.GetProperty("requisites").GetProperty("Состояние").GetString());

        var queue = await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?needsParsing=true");
        Assert.Contains(invoice, queue.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Возврат_в_черновик_решением_человека()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/draft", null);
        await OkAsync(response);

        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Черновик", view.GetProperty("requisites").GetProperty("Состояние").GetString());
    }
}
