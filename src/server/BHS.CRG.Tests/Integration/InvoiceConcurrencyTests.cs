using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Одновременная запись счёта отвечает отказом, а не удваивает строки и не ломает «разобран»
/// (issue #1173, находка ревью PR #1116).
///
/// <para><b>Как поставлена гонка.</b> Настоящую — два запроса в один миг — тестом не воспроизвести
/// наверняка: зелёный прогон не отличить от «не повезло столкнуться». Поэтому чередование собрано
/// руками, ровно как у адреса: счёт ПРОЧИТАН (своим контекстом), потом пришла чужая правка (через
/// HTTP), потом прочитанный счёт записывается. Это и есть окно между чтением и записью, только
/// растянутое до детерминированного.</para>
///
/// <para>Проверяется главное обещание: КАЖДЫЙ адрес, меняющий счёт, строки или разноску, двигает
/// версию счёта. Адрес, который этого не делает, оставляет гонку открытой молча — версия сверяется
/// только когда счёт записывается.</para>
/// </summary>
[Collection("Integration")]
public class InvoiceConcurrencyTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    /// <summary>
    /// Счёт, прочитанный ДО чужой правки, записать нельзя — какой бы адрес её ни сделал.
    /// </summary>
    [Theory]
    [InlineData("lines")]
    [InlineData("allocation")]
    [InlineData("matrix")]
    [InlineData("header")]
    [InlineData("parsed")]
    public async Task Счёт_прочитанный_до_чужой_правки_записать_нельзя(string kind)
    {
        var (client, _) = await SignInAsync("Admin");
        var (invoice, line, site) = await ReadyAsync(client);

        using var scope = host.Services.CreateScope();
        var costs = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var stale = await costs.Invoices.SingleAsync(i => i.Id == invoice);

        switch (kind)
        {
            case "lines":
                await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 2m, id: line)]);
                break;
            case "allocation":
                await AllocateAsync(client, invoice, line, [Part(site, quantity: 4)]);
                break;
            case "matrix":
                await MatrixAsync(client, invoice, line, [Part(site, quantity: 3)]);
                break;
            case "header":
                await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
                    new { requisites = await RequisitesWithAsync(client, invoice, "Назначение", "чужая правка") }));
                break;
            case "parsed":
                await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
                break;
        }

        stale.ReturnToDraft();
        var refusal = await Assert.ThrowsAsync<ConflictException>(() => costs.SaveChangesAsync());
        Assert.Contains("изменили одновременно", refusal.Message);
    }

    /// <summary>
    /// Без чужой правки прочитанный счёт записывается. Сторож самого сторожа: теория выше зелена и у
    /// контекста, который отказывает на любой записи.
    /// </summary>
    [Fact]
    public async Task Без_чужой_правки_счёт_записывается()
    {
        var (client, _) = await SignInAsync("Admin");
        var (invoice, _, _) = await ReadyAsync(client);

        using var scope = host.Services.CreateScope();
        var costs = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var read = await costs.Invoices.SingleAsync(i => i.Id == invoice);

        read.MarkParsed();
        await costs.SaveChangesAsync();

        Assert.Equal("Разобран", await StateAsync(client, invoice));
    }

    /// <summary>
    /// Повторная отправка ТОГО ЖЕ — строк и разноски — версию счёта не двигает: правки не было, и
    /// отказывать соседу не за что. Иначе форма, сохранившая без изменений, роняла бы чужой запрос.
    /// </summary>
    [Fact]
    public async Task Повторная_отправка_того_же_соседу_не_мешает()
    {
        var (client, _) = await SignInAsync("Admin");
        var (invoice, line, site) = await ReadyAsync(client);
        var part = (await ReadAsync(client, invoice)).GetProperty("lines")[0]
            .GetProperty("allocation").GetProperty("parts")[0].GetProperty("id").GetGuid();

        using var scope = host.Services.CreateScope();
        var costs = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var read = await costs.Invoices.SingleAsync(i => i.Id == invoice);

        await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 1m, id: line)]);
        await AllocateAsync(client, invoice, line, [Part(site, quantity: 10, id: part)]);
        await MatrixAsync(client, invoice, line, [Part(site, quantity: 10, id: part)]);

        read.MarkParsed();
        await costs.SaveChangesAsync();

        Assert.Equal("Разобран", await StateAsync(client, invoice));
    }

    /// <summary>
    /// Восемь одинаковых сохранений двадцати НОВЫХ строк разом — двойное нажатие, доведённое до
    /// предела. Строк в счёте остаётся двадцать, а каждый ответ — успех или 409, но не 500.
    ///
    /// <para>До правки каждый запрос читал «строк нет» и добавлял свои: набор удваивался. ⚠️ Этот тест —
    /// свидетель, а не доказательство: гонка в нём настоящая, и без защиты он краснеет «почти всегда», а
    /// не всегда. Доказательство — теория выше.</para>
    /// </summary>
    [Fact]
    public async Task Параллельные_сохранения_новых_строк_не_удваивают_набор()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        object[] lines = [.. Enumerable.Range(1, 20).Select(i => Line(cable, quantity: i, price: 1m))];

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new { lines })));

        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(20, (await ReadAsync(client, invoice)).GetProperty("lines").GetArrayLength());
    }

    /// <summary>
    /// Счёт, которому до «разобран» не хватает только решения человека: одна строка, разнесённая
    /// целиком на одну стройку.
    /// </summary>
    private async Task<(Guid Invoice, Guid Line, Guid Site)> ReadyAsync(HttpClient client)
    {
        var invoice = await CreateAsync(client, complete: true);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 1m)]);
        var (site, _) = await SiteAsync($"Стройка {Guid.NewGuid().ToString()[..6]}");
        await AllocateAsync(client, invoice, LineId(view, 1), [Part(site, quantity: 10)]);
        return (invoice, LineId(view, 1), site);
    }

    /// <summary>Записать разноску всего счёта матрицей — с отметкой версии, как её взяла бы форма.</summary>
    private static async Task MatrixAsync(HttpClient client, Guid invoice, Guid line, object[] parts)
    {
        var stamp = (await ReadAsync(client, invoice)).GetProperty("allocation").GetProperty("stamp").GetString();
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/allocation",
            new { lines = new[] { new { line, parts } }, stamp }));
    }

    private static async Task<string?> StateAsync(HttpClient client, Guid invoice) =>
        (await ReadAsync(client, invoice)).GetProperty("requisites").GetProperty("Состояние").GetString();
}
