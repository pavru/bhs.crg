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
/// Адреса сопоставления материалов и списка запомненных ресурсов: версия, права, ворота модуля (change
/// <c>plan-08-matching</c>, задача 2.3; ТЗ AUTH-8, AUTH-9). Без <c>If-Match</c> — 400, с устаревшим — 409,
/// только с правом чтения — 403.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class ResourceMatchEndpointsTests(PlanOnlyHost host)
{
    [Fact]
    public async Task Без_версии_400_устаревшая_409_и_ничего_не_записано()
    {
        var (estimate, revision, material, match) = await SetupAsync();
        var c = await EstimateSeed.ClassifierAsync(host);
        var editor = await EstimateImportFlow.EditorAsync(host);
        var stale = await RateMatchFlow.VersionAsync(editor, estimate, revision);
        var staleMatch = await MatchVersionAsync(editor, match);

        // Версии сдвигает чужая правка: сопоставление другого материала той же редакции и перенацеливание.
        var other = (await RateMatchFlow.RowsAsync(editor, revision))[0].GetProperty("materials")[1].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await editor.SendAsync(ResourceMatchingTests.Match(revision, other, stale,
            new { nomenclatureId = c.Nomenclature, remember = false }))).StatusCode);
        var moved = await new ResourceMatchingTests(host).NomenclatureAsync($"Позиция {Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.OK, (await editor.SendAsync(RateMatchFlow.With(HttpMethod.Put,
            $"/api/plan/resource-matches/{match}", staleMatch, new { nomenclatureId = moved }))).StatusCode);

        foreach (var (version, expected) in new[] { ((string?)null, HttpStatusCode.BadRequest), (stale, HttpStatusCode.Conflict) })
            Assert.Equal(expected, (await editor.SendAsync(ResourceMatchingTests.Match(revision, material, version,
                new { nomenclatureId = c.Nomenclature }))).StatusCode);
        foreach (var (version, expected) in new[] { ((string?)null, HttpStatusCode.BadRequest), (staleMatch, HttpStatusCode.Conflict) })
        {
            Assert.Equal(expected, (await editor.SendAsync(RateMatchFlow.With(HttpMethod.Put,
                $"/api/plan/resource-matches/{match}", version, new { nomenclatureId = c.Nomenclature }))).StatusCode);
            Assert.Equal(expected, (await editor.SendAsync(RateMatchFlow.With(HttpMethod.Delete,
                $"/api/plan/resource-matches/{match}", version))).StatusCode);
        }

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        Assert.Null(db.EstimateRowMaterials.Single(m => m.Id == material).NomenclatureId);
        Assert.Equal(moved, db.ResourceMatches.Single(m => m.Id == match).NomenclatureId);
    }

    /// <summary>Пустой запрос не снимает сопоставление: без позиции и без <c>clear</c> — 400.</summary>
    [Fact]
    public async Task Без_позиции_и_без_clear_400()
    {
        var (estimate, revision, material, _) = await SetupAsync();
        var editor = await EstimateImportFlow.EditorAsync(host);
        var response = await editor.SendAsync(ResourceMatchingTests.Match(revision, material,
            await RateMatchFlow.VersionAsync(editor, estimate, revision), new { }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("clear", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Только с правом чтения: список и похожие читаются, каждый пишущий адрес — 403.</summary>
    [Fact]
    public async Task Только_чтение_читает_и_не_пишет()
    {
        var (estimate, revision, material, match) = await SetupAsync();
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);

        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/plan/resource-matches")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await reader.GetAsync($"/api/plan/revisions/{revision}/resources/{material}/candidates")).StatusCode);
        foreach (var request in Writes(revision, material, match, await RateMatchFlow.VersionAsync(reader, estimate, revision)))
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(request)).StatusCode);
    }

    /// <summary>
    /// Поиск по номенклатуре для окна материала (plan-09): находит позицию по слову названия, говорит о неполноте,
    /// закрыт правом правки — читающему смету он ни к чему.
    /// </summary>
    [Fact]
    public async Task Поиск_номенклатуры_находит_и_закрыт_правом_правки()
    {
        var word = $"Провод{Guid.NewGuid():N}";
        var position = await new ResourceMatchingTests(host).NomenclatureAsync($"{word} ПуГВ 1х2,5");
        var editor = await EstimateImportFlow.EditorAsync(host);

        var response = await editor.GetAsync($"/api/plan/nomenclature?query={word}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var found = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(position, found.GetProperty("id").GetGuid());
        Assert.False(body.GetProperty("more").GetBoolean());

        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/plan/nomenclature?query={word}")).StatusCode);
    }

    /// <summary>«Забыть» убирает запомненное; уже сопоставленный им материал остаётся и говорит «забыли».</summary>
    [Fact]
    public async Task Забыть_из_списка()
    {
        var (_, _, _, match) = await SetupAsync();
        var editor = await EstimateImportFlow.EditorAsync(host);

        var response = await editor.SendAsync(RateMatchFlow.With(HttpMethod.Delete, $"/api/plan/resource-matches/{match}",
            await MatchVersionAsync(editor, match)));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = host.Services.CreateScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<PlanDbContext>().ResourceMatches.Any(m => m.Id == match));
    }

    /// <summary>Редакция с двумя несопоставленными материалами и запомненный ресурс другого кода.</summary>
    private async Task<(Guid Estimate, Guid Revision, Guid Material, Guid Match)> SetupAsync()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var (estimate, revision, material) = await new ResourceMatchingTests(host).RevisionAsync(
            new EstimateMaterialDraft("Кабель", 1m, ResourceMatchingTests.UniqueCode(), "м"),
            new EstimateMaterialDraft("Труба", 2m, ResourceMatchingTests.UniqueCode(), "м"));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var match = ResourceMatch.Create(ResourceMatchKey.Of(ResourceMatchingTests.UniqueCode(), null)!, c.Nomenclature, null, null);
        db.ResourceMatches.Add(match);
        await db.SaveChangesAsync();
        return (estimate, revision, material, match.Id);
    }

    private static async Task<string> MatchVersionAsync(HttpClient client, Guid match)
    {
        var list = await client.GetFromJsonAsync<JsonElement>("/api/plan/resource-matches?take=200");
        var total = list.GetProperty("total").GetInt32();
        for (var skip = 0; skip < total; skip += 200)
        {
            var page = skip == 0 ? list : await client.GetFromJsonAsync<JsonElement>($"/api/plan/resource-matches?take=200&skip={skip}");
            foreach (var item in page.GetProperty("items").EnumerateArray())
                if (item.GetProperty("id").GetGuid() == match) return item.GetProperty("version").GetString()!;
        }
        throw new InvalidOperationException($"Запомненного {match} в списке нет.");
    }

    /// <summary>Все пишущие адреса — с версией, чтобы отказ был от прав, а не от версии.</summary>
    internal static IEnumerable<HttpRequestMessage> Writes(Guid revision, Guid material, Guid match, string version)
    {
        var body = new { nomenclatureId = Guid.NewGuid() };
        yield return ResourceMatchingTests.Match(revision, material, version, body);
        yield return RateMatchFlow.With(HttpMethod.Put, $"/api/plan/resource-matches/{match}", "1", body);
        yield return RateMatchFlow.With(HttpMethod.Delete, $"/api/plan/resource-matches/{match}", "1");
    }
}

/// <summary>На умолчательном хосте модуль <c>plan</c> выключен: адреса материалов — отказ выключенного модуля (501).</summary>
[Collection("Integration")]
public class ResourceMatchWithoutModuleTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Модуль_выключен_501()
    {
        var client = fixture.CreateClient();
        var (revision, material) = (Guid.NewGuid(), Guid.NewGuid());
        foreach (var request in ResourceMatchEndpointsTests.Writes(revision, material, Guid.NewGuid(), "1")
                     .Append(new HttpRequestMessage(HttpMethod.Get, "/api/plan/resource-matches"))
                     .Append(new HttpRequestMessage(HttpMethod.Get, $"/api/plan/revisions/{revision}/resources/{material}/candidates"))
                     .Append(new HttpRequestMessage(HttpMethod.Get, "/api/plan/nomenclature")))
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            Assert.Equal("plan", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("module").GetString());
        }
    }
}
