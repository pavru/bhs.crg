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
    /// Очередь «Разобрать» — все недоведённые счета (issue #1292). Раньше она шла от строк без позиции,
    /// и счёт, у которого позиции подставлены, а плательщика или разноски нет, не стоял ни под одним
    /// отбором: в работе, где счёт передают из рук в руки, он терялся тихо. Уходит счёт из очереди
    /// переходом «разобран» — иначе очередь стала бы вторым реестром.
    /// </summary>
    [Fact]
    public async Task Счёт_стоит_в_очереди_пока_не_разобран_а_не_пока_строки_ждут_позиции()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);

        async Task<List<Guid>> QueueAsync() =>
            [.. (await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices?needsParsing=true"))
                .EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];

        await LinesAsync(client, invoice, [Line(null, quantity: 1, price: 1m, text: "ждёт")]);
        Assert.Contains(invoice, await QueueAsync());

        // Позиции подставлены, разноска сделана — а «Разобран» не нажат: счёт всё ещё не доведён.
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 1m)]);
        Assert.Contains(invoice, await QueueAsync());

        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
        Assert.DoesNotContain(invoice, await QueueAsync());
    }

    [Fact]
    public async Task Разобран_отказывает_пока_строка_ждёт_позиции()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true, total: 2m);

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
        var invoice = await CreateAsync(client, complete: true, total: 1m);

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
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);

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
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);
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
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m)]);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));

        var response = await client.PostAsync($"/api/costs/invoices/{invoice}/draft", null);
        await OkAsync(response);

        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Черновик", view.GetProperty("requisites").GetProperty("Состояние").GetString());
    }
    // ── Сумма к оплате обязательна (change costs-invoice-screen-01-parsed-amount, COST-9) ──────────

    /// <summary>
    /// Сумму стёрли правкой шапки у разобранного: правка ложится, счёт сам уходит в черновик, а журнал
    /// называет причину. Тем же путём, что стёртый плательщик: обязательность — из объявления типа.
    /// </summary>
    [Fact]
    public async Task Стёртая_сумма_возвращает_разобранный_счёт_в_черновик()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await ParsedAsync(client);

        var saved = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithoutTotalAsync(client, invoice) });
        await OkAsync(saved);

        var view = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Черновик", view.GetProperty("requisites").GetProperty("Состояние").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("requisites").GetProperty("Итого").ValueKind);
        Assert.Equal("правка счёта: не заполнено обязательное — «Сумма к оплате»",
            Assert.Single(await DraftJournalAsync(invoice)).After);
    }

    /// <summary>
    /// Разобранный без суммы, ставший таким до того, как сумма стала обязательной (Э3, 08.11.2026): задним
    /// числом не трогается. Читается разобранным, оплата называет пробел, а первая правка шапки — любая, не
    /// о сумме, — возвращает его в черновик той же причиной. Сумма стёрта в базе: обычной правкой
    /// разобранного без суммы не получить.
    /// </summary>
    [Fact]
    public async Task Разобранный_без_суммы_до_обновления_остаётся_разобранным_до_правки_шапки()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await ParsedAsync(client);
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database
                .ExecuteSqlAsync($"UPDATE costs.invoices SET total = NULL WHERE id = {invoice}");

        var view = await ReadAsync(client, invoice);
        Assert.Equal("Разобран", view.GetProperty("requisites").GetProperty("Состояние").GetString());
        Assert.Equal("у счёта не указана сумма к оплате", view.GetProperty("payment").GetProperty("refusal").GetString());
        Assert.Empty(await DraftJournalAsync(invoice));

        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, "Назначение", "Кабель на склад") }));

        Assert.Equal("Черновик", (await ReadAsync(client, invoice)).GetProperty("requisites").GetProperty("Состояние").GetString());
        Assert.Equal("правка счёта: не заполнено обязательное — «Сумма к оплате»",
            Assert.Single(await DraftJournalAsync(invoice)).After);
    }

    /// <summary>Разобранный счёт с суммой, равной сумме строк.</summary>
    private async Task<Guid> ParsedAsync(HttpClient client)
    {
        var invoice = await CreateAsync(client, complete: true);
        await AllocatedLinesAsync(client, invoice, [Line(cable, quantity: 1, price: 100m, rate: 20)]);
        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
        return invoice;
    }

    private static async Task<JsonElement> RequisitesWithoutTotalAsync(HttpClient client, Guid invoice)
    {
        var requisites = (await ReadAsync(client, invoice)).GetProperty("requisites");
        var patched = JsonSerializer.Deserialize<Dictionary<string, object?>>(requisites.GetRawText())!;
        patched["Итого"] = null;
        return JsonSerializer.SerializeToElement(patched);
    }

    private async Task<List<ActivityRow>> DraftJournalAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        var records = await scope.ServiceProvider.GetRequiredService<IActivityLog>()
            .ReadAsync(0, 200, ActivityVisibility.Whole, "costs.invoice.draft");
        return [.. records.Where(r => r.TargetId == invoice.ToString())];
    }
}
