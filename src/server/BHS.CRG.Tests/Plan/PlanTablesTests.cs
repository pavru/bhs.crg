using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
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
        Assert.Equal("Смета для реестра · ред. 1", row.GetProperty("Основание").GetString());
        Assert.Equal("Черновик", row.GetProperty("Статус").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("ДатаОтправки").ValueKind);
        Assert.Equal(150m, table.GetProperty("totals").GetProperty("Итого").GetProperty("sum").GetDecimal());
        Assert.Equal("Коммерческие предложения всех строек, действующие версии", table.GetProperty("boundary").GetString());
        Assert.Equal(new[] { "Версия", "ДатаОтправки", "Итого", "Номер", "Основание", "Статус", "Стройка" }.Order(),
            row.EnumerateObject().Select(p => p.Name).Order());
        var candidate = Assert.Single(await SystemCandidatesAsync(reader), c => Marker(c) == "system:table:plan.offers");
        Assert.DoesNotContain("Себестоимость", candidate.GetRawText());
    }

    [Fact]
    public async Task Работы_графика_отбираются_по_ключу_и_коду_предупреждения()
    {
        var scene = await ScheduleScenario.CreateAsync(host);
        var schedule = await scene.BuildAsync();
        var other = await scene.BuildAsync();
        var undated = await scene.ManualAsync(schedule);
        await scene.ManualAsync(schedule, "2026-11-17", "2026-11-18");
        await scene.ManualAsync(other);

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
    }

    private static object Clause(string column, string op, string value) =>
        new { type = "condition", column, op, value };

    private static string Condition(string column, string op, string value) =>
        Uri.EscapeDataString(JsonSerializer.Serialize(Clause(column, op, value)));

    private static async Task<JsonElement[]> SystemCandidatesAsync(HttpClient client) =>
        [.. (await ReadAsync(client.GetAsync("/api/datasets/system-candidates?scope=System"))).EnumerateArray()];

    private static string? Marker(JsonElement candidate) => candidate.GetProperty("sheetOrPath").GetString();
}
