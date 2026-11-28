using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using NPOI.XSSF.UserModel;
using static BHS.CRG.Tests.Plan.OfferDerivativeScenario;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class PlanTablesTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Реестр_КП_отдаёт_действующую_версию_и_итог_продажи_по_праву_чтения()
    {
        const string address = "/api/tables/plan.offers";
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction, "Смета для реестра");
        var revision = await EstimateSeed.RevisionAsync(host, estimate,
            new EstimateRowDraft("Работа", SourceVolume: 1m, SourceUnit: "шт", Total: 100m));
        using var editor = await GrantedSignIn.WithPermissionsAsync(host,
            PlanModule.EstimateRead, PlanModule.OfferRead, PlanModule.OfferEdit, PlanModule.OfferSend);
        var offer = await ReadAsync(editor.PostAsJsonAsync("/api/plan/offers", new { revisionId = revision.Id, markup = 20m }),
            HttpStatusCode.Created);
        var id = offer.GetProperty("id").GetGuid();
        var url = $"/api/plan/offers/{id}";
        var sent = await ReadAsync(SendAsync(editor, HttpMethod.Post, $"{url}/send", Version(offer), new { sentOn = "2026-11-18" }));
        var draft = await ReadAsync(SendAsync(editor, HttpMethod.Post, $"{url}/versions", Version(sent), new { }));
        await ReadAsync(SendAsync(editor, HttpMethod.Put, $"{url}/markups", Version(draft), new { markup = 50m }));

        using var denied = await GrantedSignIn.WithPermissionsAsync(host,
            PlanModule.EstimateRead, PlanModule.EstimateCost, CorePermissions.DataSetsRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync(address)).StatusCode);
        Assert.DoesNotContain(await SystemCandidatesAsync(denied), c => Marker(c) == "system:table:plan.offers");

        using var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead, CorePermissions.DataSetsRead);
        var table = await ReadAsync(reader.GetAsync($"{address}?totals=Итого&filter={Condition("Номер", "eq", offer.GetProperty("number").GetString()!)}"));
        var row = Assert.Single(table.GetProperty("rows").EnumerateArray());
        Assert.Equal(2m, row.GetProperty("Версия").GetDecimal());
        Assert.Equal(150m, row.GetProperty("Итого").GetDecimal());
        Assert.False(row.GetProperty("НеполныйИтог").GetBoolean());
        Assert.Equal("Смета для реестра · ред. 1", row.GetProperty("Основание").GetString());
        Assert.Equal("Черновик", row.GetProperty("Статус").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("ДатаОтправки").ValueKind);
        Assert.Equal(150m, table.GetProperty("totals").GetProperty("Итого").GetProperty("sum").GetDecimal());
        Assert.Equal("Коммерческие предложения всех строек, действующие версии", table.GetProperty("boundary").GetString());
        Assert.Equal(new[] { "Версия", "ДатаОтправки", "Итого", "НеполныйИтог", "Номер", "Основание", "Статус", "Стройка" }.Order(),
            row.EnumerateObject().Select(p => p.Name).Order());
        var candidate = Assert.Single(await SystemCandidatesAsync(reader), c => Marker(c) == "system:table:plan.offers");
        Assert.DoesNotContain("Себестоимость", candidate.GetRawText());
    }

    [Fact]
    public async Task КП_с_позицией_без_цены_показывает_неполный_итог_во_всех_выдачах()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var classifier = await EstimateSeed.ClassifierAsync(host);
        using var editor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead, PlanModule.OfferEdit);
        var offer = await ReadAsync(editor.PostAsJsonAsync("/api/plan/offers/from-pricelist", new
        {
            constructionId = site.Construction, date = "2000-01-01",
            lines = new[] { new { workTypeId = classifier.WorkType, volume = 1m } },
        }), HttpStatusCode.Created);

        using var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead, CorePermissions.DataSetsRead);
        var number = offer.GetProperty("number").GetString()!;
        var table = await ReadAsync(reader.GetAsync($"/api/tables/plan.offers?filter={Condition("Номер", "eq", number)}"));
        var row = Assert.Single(table.GetProperty("rows").EnumerateArray());
        Assert.Equal(0m, row.GetProperty("Итого").GetDecimal());
        Assert.True(row.GetProperty("НеполныйИтог").GetBoolean());
        var candidate = Assert.Single(await SystemCandidatesAsync(reader), c => Marker(c) == "system:table:plan.offers");
        var column = Assert.Single(candidate.GetProperty("columns").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "НеполныйИтог");
        Assert.Contains(column.GetProperty("sampleValues").EnumerateArray(), value => value.GetString() == "true");

        using var export = await reader.PostAsJsonAsync("/api/tables/plan.offers/export", new
        {
            columns = new[] { "Итого", "НеполныйИтог" },
            filter = JsonSerializer.Serialize(Clause("Номер", "eq", number)),
        });
        export.EnsureSuccessStatusCode();
        using var workbook = new XSSFWorkbook(await export.Content.ReadAsStreamAsync());
        var sheet = workbook.GetSheetAt(0);
        var header = Assert.Single(Enumerable.Range(0, sheet.LastRowNum + 1),
            index => sheet.GetRow(index)?.GetCell(1)?.ToString() == "Неполный итог");
        Assert.Equal("да", sheet.GetRow(header + 1).GetCell(1).StringCellValue);
    }

    [Fact]
    public async Task Работы_графика_отбираются_по_ключу_и_коду_предупреждения()
    {
        var scene = await ScheduleScenario.CreateAsync(host);
        var schedule = await scene.BuildAsync();
        var other = await scene.BuildAsync();
        var undated = await scene.ManualAsync(schedule);
        await scene.ManualAsync(schedule, "2026-11-17", "2026-11-18");
        var otherUndated = await scene.ManualAsync(other);

        var filter = Uri.EscapeDataString(JsonSerializer.Serialize(new
        {
            type = "group", logic = "and",
            children = new[]
            {
                Clause("КлючГрафика", "eq", ScheduleScenario.Id(schedule).ToString()),
                Clause("Предупреждения", "contains", "no-dates"),
            },
        }));
        using var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead);
        var table = await ReadAsync(reader.GetAsync($"/api/tables/plan.schedule-works?filter={filter}"));
        var row = Assert.Single(table.GetProperty("rows").EnumerateArray());
        Assert.Equal("no-dates", row.GetProperty("Предупреждения").GetString());
        Assert.Equal(ScheduleScenario.Id(undated).ToString(), Assert.Single(table.GetProperty("keys").EnumerateArray()).GetString());
        Assert.False(row.TryGetProperty("КлючГрафика", out _));
        Assert.Equal("Работы графиков всех строек", table.GetProperty("boundary").GetString());

        var any = Uri.EscapeDataString(JsonSerializer.Serialize(new
        {
            type = "group", logic = "or",
            children = new[]
            {
                Clause("КлючГрафика", "eq", ScheduleScenario.Id(schedule).ToString()),
                Clause("Предупреждения", "contains", "no-dates"),
            },
        }));
        var withOther = await ReadAsync(reader.GetAsync($"/api/tables/plan.schedule-works?filter={any}"));
        Assert.Contains(withOther.GetProperty("keys").EnumerateArray(),
            key => key.GetString() == ScheduleScenario.Id(otherUndated).ToString());
    }

    private static object Clause(string column, string op, string value) =>
        new { type = "condition", column, op, value };

    private static string Condition(string column, string op, string value) =>
        Uri.EscapeDataString(JsonSerializer.Serialize(Clause(column, op, value)));

    private static async Task<JsonElement[]> SystemCandidatesAsync(HttpClient client) =>
        [.. (await ReadAsync(client.GetAsync("/api/datasets/system-candidates?scope=System"))).EnumerateArray()];

    private static string? Marker(JsonElement candidate) => candidate.GetProperty("sheetOrPath").GetString();
}
