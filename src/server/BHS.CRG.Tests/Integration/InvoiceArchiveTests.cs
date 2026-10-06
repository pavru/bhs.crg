using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Objects;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Архив записи справочника в счетах и накладных (issue #1185, ТЗ CORE-34.4): архивную запись не
/// предлагают и новой ссылкой не принимают, а там, где она уже стоит, она остаётся — с названием и
/// пометкой.
///
/// <para>Правило записи у модуля одно на четыре двери — шапку, строки, разноску и накладную, — и
/// проверяется оно здесь через каждую: правило, которое зовут три двери из четырёх, стережёт
/// ровно то, что в него зашло.</para>
/// </summary>
[Collection("Integration")]
public class InvoiceArchiveTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost host = host;

    // ── Статьи: свой путь модуля ──────────────────────────────────────────────

    [Fact]
    public async Task Статья_уходит_в_архив_своим_адресом_и_возвращается_а_журнал_помнит_оба_шага()
    {
        // Бухгалтер: право справочника статей есть, core.catalog.edit — нет.
        var (accountant, _) = await SignInAsync("Accountant");
        var article = await ArticleAsync(accountant, "Архивный склад");
        var archivedBefore = await CountAsync(InvoiceActions.ArticleArchived);
        var returnedBefore = await CountAsync(InvoiceActions.ArticleUnarchived);

        var archived = await SetAsync(accountant, article.Id, "archive");
        Assert.True(archived.GetProperty("archived").GetBoolean());
        Assert.True(await ListedAsync(accountant, article.Id));

        // Повтор — не событие: ответ тот же, запись в журнале одна.
        await SetAsync(accountant, article.Id, "archive");
        Assert.Equal(archivedBefore + 1, await CountAsync(InvoiceActions.ArticleArchived));

        var returned = await SetAsync(accountant, article.Id, "unarchive");
        Assert.False(returned.GetProperty("archived").GetBoolean());
        Assert.False(await ListedAsync(accountant, article.Id));
        Assert.Equal(returnedBefore + 1, await CountAsync(InvoiceActions.ArticleUnarchived));
    }

    /// <summary>Узкое право не становится широким: дверь статей организацию в архив не отправит.</summary>
    [Fact]
    public async Task Дверь_статей_чужую_запись_в_архив_не_отправляет()
    {
        var (accountant, _) = await SignInAsync("Accountant");

        var response = await accountant.PostAsync($"/api/costs/articles/{supplier}/archive", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ReferenceState.Present, await StateAsync(supplier));
    }

    [Fact]
    public async Task Часть_на_архивную_статью_остаётся_с_названием_а_новая_отвергается()
    {
        var (client, _) = await SignInAsync("Admin");
        var article = await ArticleAsync(client, "Закрытый склад");
        var kept = await CreateAsync(client);
        var view = await LinesAsync(client, kept, [Line(cable, quantity: 10, price: 1m)]);
        await AllocateAsync(client, kept, LineId(view, 1), [ArticlePart(article.Id, quantity: 4)]);
        await SetAsync(client, article.Id, "archive");

        // Стоявшая часть: название на месте, потерей не названа, и правка её суммы проходит. Признак
        // архива несёт справочник статей — один источник на выбор, названия и пометку.
        var part = (await ReadAsync(client, kept)).GetProperty("lines")[0].GetProperty("allocation").GetProperty("parts")[0];
        Assert.Equal(article.Name, part.GetProperty("articleName").GetString());
        Assert.True(await ListedAsync(client, article.Id));
        Assert.False(part.GetProperty("targetLost").GetBoolean());
        await AllocateAsync(client, kept, LineId(view, 1), [ArticlePart(article.Id, quantity: 6)]);

        // Новая часть в другом счёте — отказ, и отказ называет причину, а не «такой статьи нет».
        var other = await CreateAsync(client);
        var lines = await LinesAsync(client, other, [Line(cable, quantity: 10, price: 1m)]);
        var refused = await AllocateRawAsync(client, other, LineId(lines, 1), [ArticlePart(article.Id, quantity: 4)]);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("в архиве", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Одноимённая статья в архиве — «верните её», а не «заведите»: под одним названием иначе лежали
    /// бы две статьи, и затраты одного склада разошлись бы по обеим.
    /// </summary>
    [Fact]
    public async Task Завести_статью_с_названием_архивной_нельзя_отказ_зовёт_вернуть()
    {
        var (client, _) = await SignInAsync("Admin");
        var article = await ArticleAsync(client, "Склад-двойник");
        await SetAsync(client, article.Id, "archive");

        var twin = await client.PostAsJsonAsync("/api/costs/articles", new { name = article.Name });

        Assert.Equal(HttpStatusCode.Conflict, twin.StatusCode);
        Assert.Contains("есть в архиве", await twin.Content.ReadAsStringAsync());
    }

    // ── Организации ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Список_организаций_на_выбор_архивную_скрывает_а_на_показ_отдаёт_с_признаком()
    {
        var (client, _) = await SignInAsync("Admin");
        var archived = await OrganizationAsync();
        await ArchiveAsync(archived);

        var choice = await client.GetFromJsonAsync<JsonElement>("/api/costs/organizations?purpose=choice");
        var display = await client.GetFromJsonAsync<JsonElement>("/api/costs/organizations?purpose=display");

        Assert.DoesNotContain(choice.EnumerateArray(), o => o.GetProperty("id").GetGuid() == archived);
        Assert.Contains(choice.EnumerateArray(), o => o.GetProperty("id").GetGuid() == supplier);
        Assert.True(display.EnumerateArray().Single(o => o.GetProperty("id").GetGuid() == archived)
            .GetProperty("archived").GetBoolean());
        // Умолчания нет: один адрес кормит и выбор, и показ.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/costs/organizations")).StatusCode);
    }

    [Fact]
    public async Task Счёт_с_архивным_поставщиком_читается_и_правится_а_новым_поставщиком_архивный_не_ставится()
    {
        var (client, _) = await SignInAsync("Admin");
        var organization = await OrganizationAsync();
        var invoice = await CreateAsync(client);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, "Поставщик", Reference(organization)) }));
        await ArchiveAsync(organization);

        // Название едет с ответом счёта: в списке на выбор архивной организации уже нет.
        var references = (await ReadAsync(client, invoice)).GetProperty("references");
        Assert.Equal("archived", references.GetProperty("supplier").GetString());
        Assert.StartsWith("ООО «Архив", references.GetProperty("supplierName").GetString());

        // Стоявшая ссылка принимается: поправить в счёте что-то другое можно.
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, "Назначение", "поправили") }));

        // Реестр называет поставщика и ставит признак.
        var item = (await client.GetFromJsonAsync<JsonElement>("/api/costs/invoices")).EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == invoice);
        Assert.True(item.GetProperty("supplierArchived").GetBoolean());
        Assert.NotNull(item.GetProperty("supplierName").GetString());

        // Новая ссылка — в другом счёте и при создании — отвергается.
        var other = await CreateAsync(client);
        var refused = await client.PutAsJsonAsync($"/api/costs/invoices/{other}",
            new { requisites = await RequisitesWithAsync(client, other, "Плательщик", Reference(organization)) });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        // Слова — МОДУЛЯ, одни у всех его адресов. С правилом архива в охране ядра (issue #1185) та
        // же ссылка отвергалась бы раньше и другими словами; порядок проверок это и стережёт.
        const string OwnWords = "организация в архиве — в выборе её нет";
        Assert.Contains("«Плательщик»: " + OwnWords, await refused.Content.ReadAsStringAsync());

        var created = await client.PostAsJsonAsync("/api/costs/invoices", new
        {
            requisites = new Dictionary<string, object?> { ["Номер"] = "СЧ-архив", ["Поставщик"] = Reference(organization) },
        });
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Contains("«Поставщик»: " + OwnWords, await created.Content.ReadAsStringAsync());
    }

    // ── Номенклатура: строки счёта и накладной ────────────────────────────────

    [Fact]
    public async Task Строка_счёта_с_архивной_позицией_остаётся_а_новая_строка_её_не_принимает()
    {
        var (client, _) = await SignInAsync("Admin");
        var position = await PositionAsync();
        var invoice = await CreateAsync(client);
        var view = await LinesAsync(client, invoice, [Line(position, quantity: 1, price: 1m)]);
        await ArchiveAsync(position);

        // Стоявшая строка: названа, помечена, потерей не считается — и правка её цены проходит.
        var line = (await ReadAsync(client, invoice)).GetProperty("lines")[0];
        Assert.True(line.GetProperty("nomenclatureArchived").GetBoolean());
        Assert.False(line.GetProperty("nomenclatureLost").GetBoolean());
        Assert.NotNull(line.GetProperty("nomenclatureName").GetString());
        await LinesAsync(client, invoice, [Line(position, quantity: 1, price: 2m, id: LineId(view, 1))]);

        var other = await CreateAsync(client);
        var refused = await client.PutAsJsonAsync($"/api/costs/invoices/{other}/lines",
            new { lines = new object[] { Line(cable, quantity: 1, price: 1m), Line(position, quantity: 1, price: 1m) } });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var said = await refused.Content.ReadAsStringAsync();
        Assert.Contains("Строка 2", said);
        Assert.Contains("в архиве", said);
    }

    /// <summary>
    /// Накладная — четвёртая дверь того же правила. Раньше у неё не было и «стоявшую принять»:
    /// черновик со старой позицией не сохранялся, пока позицию не заменят.
    /// </summary>
    [Fact]
    public async Task Накладная_стоявшую_архивную_позицию_держит_новую_не_принимает()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync($"Накладная архива {Guid.NewGuid().ToString("N")[..6]}");
        var position = await PositionAsync();
        var waybill = await WaybillAsync(client, site, position);
        var id = waybill.GetProperty("id").GetGuid();
        var lineId = waybill.GetProperty("lines")[0].GetProperty("id").GetGuid();
        await ArchiveAsync(position);

        // Та же позиция в той же накладной: набор со второй строкой сохраняется.
        var kept = await WaybillLinesAsync(client, id,
            WaybillRow(position, id: lineId), WaybillRow(cable));
        await OkAsync(kept);
        var line = (await kept.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lines")[0];
        Assert.True(line.GetProperty("nomenclatureArchived").GetBoolean());
        Assert.False(line.GetProperty("nomenclatureLost").GetBoolean());

        // Новая накладная архивную позицию не принимает — ни набором строк, ни сопоставлением строки.
        var other = await WaybillAsync(client, site, null);
        var otherId = other.GetProperty("id").GetGuid();
        var refused = await WaybillLinesAsync(client, otherId, WaybillRow(position));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("в архиве", await refused.Content.ReadAsStringAsync());

        var matched = await client.PutAsJsonAsync(
            $"/api/costs/waybills/{otherId}/lines/{other.GetProperty("lines")[0].GetProperty("id").GetGuid()}/nomenclature",
            new { nomenclature = Reference(position) });
        Assert.Equal(HttpStatusCode.BadRequest, matched.StatusCode);
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private static async Task<JsonElement> SetAsync(HttpClient client, Guid article, string action)
    {
        var response = await client.PostAsync($"/api/costs/articles/{article}/{action}", null);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Признак статьи в справочнике: список отдаёт все статьи, архивные — с признаком.</summary>
    private static async Task<bool> ListedAsync(HttpClient client, Guid article) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/costs/articles")).EnumerateArray()
            .Single(a => a.GetProperty("id").GetGuid() == article).GetProperty("archived").GetBoolean();

    private async Task<Guid> OrganizationAsync() =>
        await EntryAsync(await TypeAsync(CostsRecordTypes.OrganizationCode, "Организация"),
            $"ООО «Архив {Guid.NewGuid().ToString("N")[..6]}»");

    private async Task<Guid> PositionAsync() =>
        await EntryAsync(await TypeAsync(CostsRecordTypes.NomenclatureCode, "Номенклатура"),
            $"Автомат архивный {Guid.NewGuid().ToString("N")[..6]}");

    private async Task ArchiveAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        Assert.Equal(ArchiveOutcome.Changed,
            await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(id, archived: true));
    }

    private async Task<ReferenceState> StateAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IModuleReferenceTargets>()
            .StatesAsync(BHS.CRG.Modules.Data.ReferenceTarget.Record, [id]))[id];
    }

    private async Task<int> CountAsync(ModuleActivityAction action)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<BHS.CRG.Application.Activity.IActivityLog>()
            .CountAsync(BHS.CRG.Application.Activity.ActivityVisibility.Whole, action.Code);
    }

    private static Dictionary<string, object?> WaybillRow(Guid? nomenclature, Guid? id = null) => new()
    {
        ["id"] = id?.ToString(),
        ["nomenclature"] = nomenclature is { } value ? Reference(value) : null,
        ["sourceText"] = "Строка накладной",
        ["unit"] = "шт",
        ["quantity"] = 1m,
    };

    /// <summary>Черновик накладной с одной строкой.</summary>
    private static async Task<JsonElement> WaybillAsync(HttpClient client, Guid site, Guid? position)
    {
        var created = await client.PostAsJsonAsync("/api/costs/waybills", new Dictionary<string, object?>
        {
            ["number"] = $"РН-{Guid.NewGuid().ToString()[..6]}",
            ["issuedOn"] = "2026-09-03",
            ["construction"] = site.ToString(),
        });
        await OkAsync(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var lines = await WaybillLinesAsync(client, id, WaybillRow(position));
        await OkAsync(lines);
        return await lines.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<HttpResponseMessage> WaybillLinesAsync(
        HttpClient client, Guid id, params Dictionary<string, object?>[] rows)
    {
        var version = (await client.GetFromJsonAsync<JsonElement>($"/api/costs/waybills/{id}"))
            .GetProperty("version").GetString();
        return await client.PutAsJsonAsync($"/api/costs/waybills/{id}/lines", new { ifMatch = version, lines = rows });
    }
}
