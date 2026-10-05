using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Расходная накладная: ввод, проведение и «материалы на объекте» (задача D1 этапа 2, issue #1083;
/// ТЗ COST-5, COST-17).
///
/// <para>Главный сторож — первый тест: строка без позиции номенклатуры в перечень отпущенного не
/// попадает, и счётчик её называет. Одно без другого — дефект: перечень без оговорки читался бы как
/// «выдано только это».</para>
/// </summary>
[Collection("Integration")]
public class WaybillTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    [Fact]
    public async Task Строка_без_номенклатуры_в_материалы_не_попадает_и_названа_числом()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: материалы");

        var first = await PostedAsync(client, site, "2026-09-03",
            Row(cable, 100m, "м"), Row(null, 5m, "шт", "Хомут нейлоновый"), Row(null, 2m, "уп", "Дюбель"));
        // Вторая накладная на ту же стройку: количество складывается, даты раздвигаются.
        await PostedAsync(client, site, "2026-09-10", Row(cable, 50m, "м"), Row(conduit, 30m, "м"));

        // Число видно на самой накладной…
        Assert.Equal(2, first.GetProperty("totals").GetProperty("unmatched").GetInt32());
        Assert.Equal(3, first.GetProperty("totals").GetProperty("count").GetInt32());

        // …и рядом с перечнем: одним ответом, а не отдельным запросом, который можно не сделать.
        var materials = await MaterialsAsync(client, site);
        var items = materials.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.GetProperty("name").GetString() is null);
        var issuedCable = Assert.Single(items, i => i.GetProperty("nomenclatureId").GetGuid() == cable);
        Assert.Equal(150m, issuedCable.GetProperty("quantity").GetDecimal());
        Assert.Equal("2026-09-03", issuedCable.GetProperty("first").GetString());
        Assert.Equal("2026-09-10", issuedCable.GetProperty("last").GetString());
        Assert.Equal(2, issuedCable.GetProperty("waybills").GetInt32());

        Assert.Equal(2, materials.GetProperty("unmatchedLines").GetInt32());
        Assert.Equal(1, materials.GetProperty("unmatchedWaybills").GetInt32());

        // Отбор «свести со справочником» находит именно первую.
        var queue = await ListAsync(client, $"?constructionId={site}&unmatched=true");
        Assert.Equal(first.GetProperty("id").GetGuid(), Assert.Single(queue).GetProperty("id").GetGuid());
        Assert.Equal(2, queue[0].GetProperty("unmatched").GetInt32());
    }

    /// <summary>
    /// Накладная из 1С приходит проведённой и с несопоставленными строками: свести их надо, не
    /// распроводя документ, — и сведённая строка появляется в перечне.
    /// </summary>
    [Fact]
    public async Task Сопоставление_строки_проведённой_накладной_переносит_её_в_материалы()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: сопоставление");
        var waybill = await PostedAsync(client, site, "2026-09-05", Row(null, 12m, "м", "Труба гофр. 20"));
        var id = waybill.GetProperty("id").GetGuid();
        var line = waybill.GetProperty("lines")[0].GetProperty("id").GetGuid();

        Assert.Empty((await MaterialsAsync(client, site)).GetProperty("items").EnumerateArray());

        var matched = await client.PutAsJsonAsync($"/api/costs/waybills/{id}/lines/{line}/nomenclature",
            new { nomenclature = Reference(conduit) });
        await OkAsync(matched);
        var view = await matched.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Posted", view.GetProperty("state").GetString());
        Assert.Equal(0, view.GetProperty("totals").GetProperty("unmatched").GetInt32());
        // Цитата из бумаги остаётся: сопоставление её не затирает.
        Assert.Equal("Труба гофр. 20", view.GetProperty("lines")[0].GetProperty("sourceText").GetString());

        var materials = await MaterialsAsync(client, site);
        Assert.Equal(12m, Assert.Single(materials.GetProperty("items").EnumerateArray()).GetProperty("quantity").GetDecimal());
        Assert.Equal(0, materials.GetProperty("unmatchedLines").GetInt32());
    }

    [Fact]
    public async Task Черновик_в_материалы_не_попадает_ни_перечнем_ни_счётчиком()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: черновик");
        var id = await DraftAsync(client, site, "2026-09-07", Row(cable, 10m, "м"), Row(null, 1m, "шт", "Что-то"));

        var materials = await MaterialsAsync(client, site);
        Assert.Empty(materials.GetProperty("items").EnumerateArray());
        Assert.Equal(0, materials.GetProperty("unmatchedLines").GetInt32());

        // Проведена — попала; возвращена в черновик — ушла обратно.
        await OkAsync(await client.PostAsync($"/api/costs/waybills/{id}/posted", null));
        Assert.Single((await MaterialsAsync(client, site)).GetProperty("items").EnumerateArray());

        await OkAsync(await client.PostAsync($"/api/costs/waybills/{id}/draft", null));
        materials = await MaterialsAsync(client, site);
        Assert.Empty(materials.GetProperty("items").EnumerateArray());
        Assert.Equal(0, materials.GetProperty("unmatchedLines").GetInt32());
    }

    [Fact]
    public async Task Проведение_отказывает_накладной_по_которой_выданное_нечем_посчитать()
    {
        var (client, _) = await SignInAsync("Supplier");

        // Ни номера, ни даты, ни стройки, ни строк.
        var created = await client.PostAsJsonAsync("/api/costs/waybills", new { warehouse = "Основной" });
        await OkAsync(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var refused = await client.PostAsync($"/api/costs/waybills/{id}/posted", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var why = await refused.Content.ReadAsStringAsync();
        foreach (var named in new[] { "номер", "дата отпуска", "стройка-получатель", "ни одной строки" })
            Assert.Contains(named, why);

        // Строка без количества — тоже отказ, и он называет её номер.
        var (site, _) = await SiteAsync("Накладная: количество");
        var draft = await DraftAsync(client, site, "2026-09-08", Row(cable, 1m, "м"), Row(conduit, null, "м"));
        refused = await client.PostAsync($"/api/costs/waybills/{draft}/posted", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("строка 2", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Проведённую накладную не правят: её строки уже в перечне, и тихая правка количества меняла бы
    /// его без следа. Возврат в черновик правку открывает — и пишется в журнал.
    /// </summary>
    [Fact]
    public async Task Проведённая_накладная_правится_только_через_возврат_в_черновик()
    {
        var (client, _) = await SignInAsync("Supplier");
        var (site, _) = await SiteAsync("Накладная: правка");
        var id = (await PostedAsync(client, site, "2026-09-09", Row(cable, 10m, "м"))).GetProperty("id").GetGuid();

        var lines = await client.PutAsJsonAsync($"/api/costs/waybills/{id}/lines", new { lines = new[] { Row(cable, 99m, "м") } });
        Assert.Equal(HttpStatusCode.Conflict, lines.StatusCode);
        Assert.Contains("Верните накладную в черновик", await lines.Content.ReadAsStringAsync());

        var header = await client.PutAsJsonAsync($"/api/costs/waybills/{id}", Header(site, "2026-09-01"));
        Assert.Equal(HttpStatusCode.Conflict, header.StatusCode);

        Assert.Equal(10m, Assert.Single((await MaterialsAsync(client, site)).GetProperty("items").EnumerateArray())
            .GetProperty("quantity").GetDecimal());

        await OkAsync(await client.PostAsync($"/api/costs/waybills/{id}/draft", null));
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/waybills/{id}/lines", new { lines = new[] { Row(cable, 99m, "м") } }));
    }

    /// <summary>Право на накладные — не право на счета, и наоборот (ТЗ COST-29).</summary>
    [Fact]
    public async Task Накладные_закрыты_своим_правом_а_денег_в_ответе_нет()
    {
        var (supplier, _) = await SignInAsync("Supplier");
        var (accountant, _) = await SignInAsync("Accountant");
        var (engineer, _) = await SignInAsync("User");
        var (site, _) = await SiteAsync("Накладная: права");
        var waybill = await PostedAsync(supplier, site, "2026-09-11", Row(cable, 3m, "м"));

        foreach (var closed in new[] { accountant, engineer })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await closed.GetAsync("/api/costs/waybills")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden,
                (await closed.GetAsync($"/api/costs/materials?constructionId={site}")).StatusCode);
        }

        // Денег нет ни в накладной, ни в перечне — и взяться им неоткуда: колонок с суммой у таблиц нет.
        var shown = waybill.GetRawText() + (await MaterialsAsync(supplier, site)).GetRawText();
        // Ключ целиком, с закрывающей кавычкой: «totals» — счётчики строк, а не итог.
        foreach (var money in new[] { "price", "amount", "total", "vatAmount", "vatTotal", "sum" })
            Assert.DoesNotContain($"\"{money}\"", shown, StringComparison.OrdinalIgnoreCase);

        using var scope = host.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<CostsDbContext>().Model;
        foreach (var entity in new[] { typeof(Waybill), typeof(WaybillLine) })
            Assert.DoesNotContain(model.FindEntityType(entity)!.GetProperties(),
                p => p.GetColumnName() is "price" or "amount" or "total" or "vat_amount" or "vat_total");
    }

    [Fact]
    public async Task Ссылка_в_пустоту_не_записывается()
    {
        var (client, _) = await SignInAsync("Supplier");

        var lostSite = await client.PostAsJsonAsync("/api/costs/waybills", Header(Guid.NewGuid(), "2026-09-12"));
        Assert.Equal(HttpStatusCode.BadRequest, lostSite.StatusCode);
        Assert.Contains("Стройки", await lostSite.Content.ReadAsStringAsync());

        var (site, _) = await SiteAsync("Накладная: ссылки");
        var id = await DraftAsync(client, site, "2026-09-12");
        var lostPosition = await client.PutAsJsonAsync($"/api/costs/waybills/{id}/lines",
            new { lines = new[] { Row(cable, 1m, "м"), Row(Guid.NewGuid(), 1m, "м") } });
        Assert.Equal(HttpStatusCode.BadRequest, lostPosition.StatusCode);
        Assert.Contains("строка 2", await lostPosition.Content.ReadAsStringAsync());
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Row(Guid? nomenclature, decimal? quantity, string unit, string? text = null) =>
        new()
        {
            ["nomenclature"] = nomenclature is { } value ? Reference(value) : null,
            ["sourceText"] = text,
            ["unit"] = unit,
            ["quantity"] = quantity,
        };

    private static Dictionary<string, object?> Header(Guid site, string issuedOn) => new()
    {
        ["number"] = $"РН-{Guid.NewGuid().ToString()[..6]}",
        ["issuedOn"] = issuedOn,
        ["warehouse"] = "Основной склад",
        ["construction"] = site.ToString(),
        ["receivedBy"] = "Прораб Иванов",
    };

    private static async Task<Guid> DraftAsync(
        HttpClient client, Guid site, string issuedOn, params Dictionary<string, object?>[] rows)
    {
        var created = await client.PostAsJsonAsync("/api/costs/waybills", Header(site, issuedOn));
        await OkAsync(created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        if (rows.Length > 0)
            await OkAsync(await client.PutAsJsonAsync($"/api/costs/waybills/{id}/lines", new { lines = rows }));
        return id;
    }

    private static async Task<JsonElement> PostedAsync(
        HttpClient client, Guid site, string issuedOn, params Dictionary<string, object?>[] rows)
    {
        var id = await DraftAsync(client, site, issuedOn, rows);
        var posted = await client.PostAsync($"/api/costs/waybills/{id}/posted", null);
        await OkAsync(posted);
        return await posted.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> MaterialsAsync(HttpClient client, Guid site)
    {
        var response = await client.GetAsync($"/api/costs/materials?constructionId={site}");
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync("/api/costs/waybills" + query);
        await OkAsync(response);
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()];
    }
}
