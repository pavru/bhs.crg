using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Адреса сопоставления и списка запомненного: версия, права, ворота модуля (change <c>plan-08-matching</c>,
/// задача 1.6; ТЗ AUTH-8, AUTH-9). Без <c>If-Match</c> — 400, с устаревшим — 409, только с правом чтения — 403.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class RateMatchEndpointsTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Без_версии_400_устаревшая_409_и_ничего_не_записано()
    {
        var (estimate, revision, row, match) = await SetupAsync();
        var editor = await EstimateImportFlow.EditorAsync(host);
        var c = await EstimateSeed.ClassifierAsync(host);
        var stale = await RateMatchFlow.VersionAsync(editor, estimate, revision);
        var staleMatch = await MatchVersionAsync(editor, match);

        // Версию сдвигает чужая правка: сопоставление другой строки той же редакции и перенацеливание запомненного.
        var other = (await RateMatchFlow.RowsAsync(editor, revision))[1].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await editor.SendAsync(RateMatchFlow.MatchRow(revision, other, stale,
            new { workTypeId = c.WorkType }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await editor.SendAsync(RateMatchFlow.With(HttpMethod.Put,
            $"/api/plan/rate-matches/{match}", staleMatch, new { workTypeId = c.WorkType, unitId = c.Unit, factor = 10m }))).StatusCode);

        foreach (var (version, expected) in new[] { ((string?)null, HttpStatusCode.BadRequest), (stale, HttpStatusCode.Conflict) })
        {
            Assert.Equal(expected, (await editor.SendAsync(RateMatchFlow.MatchRow(revision, row, version,
                new { workTypeId = c.WorkType }))).StatusCode);
            Assert.Equal(expected, (await editor.SendAsync(RateMatchFlow.MatchSection(revision, version,
                new { lsrSectionCode = "1", workTypeId = c.WorkType }))).StatusCode);
        }
        foreach (var (version, expected) in new[] { ((string?)null, HttpStatusCode.BadRequest), (staleMatch, HttpStatusCode.Conflict) })
        {
            Assert.Equal(expected, (await editor.SendAsync(RateMatchFlow.With(HttpMethod.Put, $"/api/plan/rate-matches/{match}",
                version, new { workTypeId = c.WorkType }))).StatusCode);
            Assert.Equal(expected, (await editor.SendAsync(RateMatchFlow.With(HttpMethod.Delete, $"/api/plan/rate-matches/{match}",
                version))).StatusCode);
        }

        Assert.Null(RateMatchFlow.Stored(host, row).WorkTypeId);
        using var scope = host.Services.CreateScope();
        Assert.Equal(10m, scope.ServiceProvider.GetRequiredService<PlanDbContext>().RateMatches.Single(m => m.Id == match).Factor);
    }

    /// <summary>Только с правом чтения: список читается, каждый пишущий адрес — 403.</summary>
    [Fact]
    public async Task Только_чтение_читает_список_и_не_пишет()
    {
        var (estimate, revision, row, match) = await SetupAsync();
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/plan/rate-matches")).StatusCode);
        foreach (var request in Writes(revision, row, match, await RateMatchFlow.VersionAsync(reader, estimate, revision)))
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(request)).StatusCode);
    }

    /// <summary>Перенацелить: вид работы, единица и коэффициент; запомненное в списке — с названиями и версией.</summary>
    [Fact]
    public async Task Перенацелить_из_списка()
    {
        var (_, _, _, match) = await SetupAsync();
        var c = await EstimateSeed.ClassifierAsync(host);
        var editor = await EstimateImportFlow.EditorAsync(host);

        var response = await editor.SendAsync(RateMatchFlow.With(HttpMethod.Put, $"/api/plan/rate-matches/{match}",
            await MatchVersionAsync(editor, match), new { workTypeId = c.WorkType, unitId = c.Piece, factor = 2m }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("шт", view.GetProperty("unitName").GetString());
        Assert.Equal("Прокладка кабеля", view.GetProperty("workTypeName").GetString());
        Assert.Equal(2m, view.GetProperty("factor").GetDecimal());
        Assert.Equal("100 м", view.GetProperty("sourceUnit").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("issue").ValueKind);
    }

    /// <summary>Редакция со строкой раздела «1» и запомненным шифром другой строки.</summary>
    private async Task<(Guid Estimate, Guid Revision, Guid Row, Guid Match)> SetupAsync()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = (await EstimateSeed.RevisionAsync(host, estimate,
            RateMatchingTests.Row(RateMatchFlow.UniqueCode(), 1m, "100 м", "1"),
            RateMatchingTests.Row(RateMatchFlow.UniqueCode(), 2m, "100 м", "2"))).Id;
        var editor = await EstimateImportFlow.EditorAsync(host);
        var rows = await RateMatchFlow.RowsAsync(editor, revision);

        var c = await EstimateSeed.ClassifierAsync(host);
        var code = RateMatchFlow.UniqueCode();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var match = RateMatch.Create(RateMatchKey.Of(code, null)!, new RateMatchValue(c.WorkType, c.Unit, 100m, "100 м"), null, null);
        db.RateMatches.Add(match);
        await db.SaveChangesAsync();
        return (estimate, revision, rows[0].GetProperty("id").GetGuid(), match.Id);
    }

    private static async Task<string> MatchVersionAsync(HttpClient client, Guid match)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/api/plan/rate-matches?take=200");
        var total = list.GetProperty("total").GetInt32();
        for (var skip = 0; skip < total; skip += 200)
        {
            var page = skip == 0 ? list : await client.GetFromJsonAsync<JsonElement>($"/api/plan/rate-matches?take=200&skip={skip}");
            foreach (var item in page.GetProperty("items").EnumerateArray())
                if (item.GetProperty("id").GetGuid() == match) return item.GetProperty("version").GetString()!;
        }
        throw new InvalidOperationException($"Запомненного {match} в списке нет.");
    }

    /// <summary>Все пишущие адреса сопоставления — с версией, чтобы отказ был от прав, а не от версии.</summary>
    internal static IEnumerable<HttpRequestMessage> Writes(Guid revision, Guid row, Guid match, string version)
    {
        var body = new { workTypeId = Guid.NewGuid() };
        yield return RateMatchFlow.MatchRow(revision, row, version, body);
        yield return RateMatchFlow.MatchSection(revision, version, new { lsrSectionCode = "1", workTypeId = Guid.NewGuid() });
        yield return RateMatchFlow.With(HttpMethod.Put, $"/api/plan/rate-matches/{match}", "1", body);
        yield return RateMatchFlow.With(HttpMethod.Delete, $"/api/plan/rate-matches/{match}", "1");
    }
}

/// <summary>На умолчательном хосте модуль <c>plan</c> выключен: адреса сопоставления — отказ выключенного модуля (501).</summary>
[Collection("Integration")]
public class RateMatchWithoutModuleTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Модуль_выключен_501()
    {
        var client = fixture.CreateClient();
        foreach (var request in RateMatchEndpointsTests.Writes(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "1")
                     .Append(new HttpRequestMessage(HttpMethod.Get, "/api/plan/rate-matches")))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            Assert.Equal("plan", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("module").GetString());
        }
    }
}
