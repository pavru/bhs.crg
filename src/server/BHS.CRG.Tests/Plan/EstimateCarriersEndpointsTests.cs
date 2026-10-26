using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Носители экранов смет — адреса чтения и массовой правки (change <c>plan-31-estimate-ui-carriers</c>, PR 2; ТЗ
/// PLAN-6.1, PLAN-20, PLAN-5.2, PLAN-9.1, AUTH-8, AUTH-9): разделы ЛСР, общий список смет, предпросмотр смены
/// действующей, коэффициент до записи, массовое сопоставление строк и материалов по материалу.
///
/// <para>⚠️ Запомненное общее на базу хоста (<see cref="RateMatchFlow" />): сопоставления здесь — с
/// <c>remember: false</c>, иначе шифры и ресурсы ЭОМ, запомненные одним тестом, подставились бы в импорт другого.</para>
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class EstimateCarriersEndpointsTests(PlanOnlyHost host)
{
    private const string Eom = "lsr-16.2.2-eom";

    /// <summary>Разделы ЛСР одним ответом, в порядке файла, со счётчиками, которые сходятся со строками (D5).</summary>
    [Fact]
    public async Task Разделы_редакции_одним_ответом_счётчики_сходятся_со_строками()
    {
        var (_, revision) = await ImportAsync();
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);
        var rows = await RateMatchFlow.RowsAsync(reader, revision);

        var sections = (await reader.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{revision}/sections"))
            .GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["1", "2", "3", "4", "5", "6"], sections.Select(s => s.GetProperty("code").GetString()));
        Assert.Equal(rows.Count, sections.Sum(s => s.GetProperty("rows").GetInt32()));
        foreach (var section in sections)
        {
            var own = rows.Where(r => r.GetProperty("lsrSection").GetProperty("code").GetString() == section.GetProperty("code").GetString()).ToList();
            Assert.Equal(own[0].GetProperty("lsrSection").GetProperty("name").GetString(), section.GetProperty("name").GetString());
            Assert.Equal(own.Count, section.GetProperty("rows").GetInt32());
            Assert.Equal(own.Count(r => RateMatchFlow.Marks(r).Contains("unmatched")), section.GetProperty("unmatched").GetInt32());
            Assert.Equal(own.SelectMany(r => r.GetProperty("materials").EnumerateArray())
                    .Count(m => m.GetProperty("nomenclatureId").ValueKind == JsonValueKind.Null),
                section.GetProperty("materialsUnmatched").GetInt32());
            var work = own.Where(r => r.GetProperty("kind").GetString() == "work").ToList();
            var units = section.GetProperty("units").EnumerateArray().ToList();
            Assert.Equal(work.Count, units.Sum(u => u.GetProperty("rows").GetInt32()));
            Assert.All(units, u => Assert.Equal(
                work.Count(r => r.GetProperty("sourceUnit").GetString() == u.GetProperty("sourceUnit").GetString()),
                u.GetProperty("rows").GetInt32()));
        }
    }

    /// <summary>Без стройки — сметы всех строек страницами, со стройкой и разделом; без права денег нет (D6).</summary>
    [Fact]
    public async Task Общий_список_смет_страницами_без_денег_без_права()
    {
        var first = await EstimateSeed.SiteAsync(host, "Первая");
        var second = await EstimateSeed.SiteAsync(host, "Вторая");
        var a = await EstimateSeed.EstimateAsync(host, first.Construction, "А");
        var b = await EstimateSeed.EstimateAsync(host, second.Construction, "Б");
        await EstimateSeed.RevisionAsync(host, b, await EstimateSeed.MatchedRowAsync(host, second, 10m));
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);

        var seen = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await reader.GetFromJsonAsync<JsonElement>(
                "/api/plan/estimates?limit=200" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}"));
            Assert.Equal("cost", Assert.Single(page.GetProperty("hidden").EnumerateArray()).GetProperty("code").GetString());
            seen.AddRange(page.GetProperty("items").EnumerateArray());
            cursor = page.TryGetProperty("next", out var next) ? next.GetString() : null;
        }
        while (cursor is not null);

        Assert.Equal(seen.Count, seen.Select(e => e.GetProperty("id").GetGuid()).Distinct().Count());
        var ea = Assert.Single(seen, e => e.GetProperty("id").GetGuid() == a);
        var eb = Assert.Single(seen, e => e.GetProperty("id").GetGuid() == b);
        Assert.Equal(first.Construction, ea.GetProperty("constructionId").GetGuid());
        Assert.Equal(second.Construction, eb.GetProperty("constructionId").GetGuid());
        Assert.True(eb.TryGetProperty("sectionId", out _));
        var revision = Assert.Single(eb.GetProperty("revisions").EnumerateArray());
        Assert.Equal(1, revision.GetProperty("rows").GetInt32());
        Assert.False(revision.TryGetProperty("cost", out _));

        // Курсор: страница из одной сметы и следующая — другая, без повторов.
        var one = await reader.GetFromJsonAsync<JsonElement>("/api/plan/estimates?limit=1");
        var after = await reader.GetFromJsonAsync<JsonElement>(
            $"/api/plan/estimates?limit=1&cursor={Uri.EscapeDataString(one.GetProperty("next").GetString()!)}");
        Assert.NotEqual(Assert.Single(one.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid(),
            Assert.Single(after.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync("/api/plan/estimates?cursor=мусор")).StatusCode);

        // С правом на себестоимость — деньги на месте.
        var estimator = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, PlanModule.EstimateCost);
        var all = await estimator.GetFromJsonAsync<JsonElement>("/api/plan/estimates?limit=200");
        Assert.False(all.TryGetProperty("hidden", out _));
    }

    /// <summary>
    /// Предпросмотр смены действующей (D7): только изменившиеся объёмы, действующая не меняется, деньги — с правом;
    /// с одним правом чтения — 403. Версия из ответа годится для самой смены.
    /// </summary>
    [Fact]
    public async Task Предпросмотр_смены_действующей()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var first = await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 10m));
        var second = await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 25m),
            new EstimateRowDraft("Без вида работы"));
        var same = await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 10m));
        var c = await EstimateSeed.ClassifierAsync(host);
        var editor = await EstimateImportFlow.EditorAsync(host);

        var preview = await editor.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}/current-revision/preview?revisionId={second.Id}");
        Assert.Equal(2, preview.GetProperty("revisionNumber").GetInt32());
        Assert.Equal(1, preview.GetProperty("currentRevisionNumber").GetInt32());
        var change = Assert.Single(preview.GetProperty("changes").EnumerateArray());
        Assert.Equal(c.WorkType, change.GetProperty("workTypeId").GetGuid());
        Assert.Equal(c.Unit, change.GetProperty("unitId").GetGuid());
        Assert.Equal(10m, change.GetProperty("was").GetDecimal());
        Assert.Equal(25m, change.GetProperty("becomes").GetDecimal());
        Assert.Equal(1, preview.GetProperty("unmatched").GetInt32());
        Assert.False(preview.TryGetProperty("cost", out _));
        Assert.Equal("cost", Assert.Single(preview.GetProperty("hidden").EnumerateArray()).GetProperty("code").GetString());
        Assert.Equal(first.Id, await EstimateSeed.CurrentRevisionAsync(host, estimate));

        // Тот же объём — изменившегося нет.
        var unchanged = await editor.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}/current-revision/preview?revisionId={same.Id}");
        Assert.Empty(unchanged.GetProperty("changes").EnumerateArray());

        var estimator = await EstimateImportFlow.CostEditorAsync(host);
        var withCost = await estimator.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}/current-revision/preview?revisionId={second.Id}");
        Assert.Equal(100m, withCost.GetProperty("cost").GetProperty("was").GetDecimal());
        Assert.Equal(250m, withCost.GetProperty("cost").GetProperty("becomes").GetDecimal());

        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.GetAsync($"/api/plan/estimates/{estimate}/current-revision/preview?revisionId={second.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await editor.GetAsync($"/api/plan/estimates/{estimate}/current-revision/preview?revisionId={Guid.NewGuid()}")).StatusCode);

        // Смена — та же одна правка, с версией сметы из предпросмотра.
        var make = RateMatchFlow.With(HttpMethod.Post, $"/api/plan/estimates/{estimate}/current-revision",
            preview.GetProperty("version").GetString(), new { revisionId = second.Id });
        Assert.Equal(HttpStatusCode.OK, (await editor.SendAsync(make)).StatusCode);
        Assert.Equal(second.Id, await EstimateSeed.CurrentRevisionAsync(host, estimate));
    }

    /// <summary>Коэффициент до записи (D8): «100 шт» → «шт» = 100; «м» → «шт» — пусто с причиной, а не 1.</summary>
    [Fact]
    public async Task Коэффициент_до_записи()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);

        var scaled = await reader.GetFromJsonAsync<JsonElement>($"/api/plan/unit-factor?sourceUnit={Uri.EscapeDataString("100 шт")}&unitId={c.Piece}");
        Assert.Equal(100m, scaled.GetProperty("factor").GetDecimal());
        Assert.Equal("100 шт", scaled.GetProperty("sourceUnit").GetString());
        Assert.Equal("шт", scaled.GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.Null, scaled.GetProperty("reason").ValueKind);

        var foreign = await reader.GetFromJsonAsync<JsonElement>($"/api/plan/unit-factor?sourceUnit=м&unitId={c.Piece}");
        Assert.Equal(JsonValueKind.Null, foreign.GetProperty("factor").ValueKind);
        Assert.Contains("не подсказывают", foreign.GetProperty("reason").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync($"/api/plan/unit-factor?sourceUnit=м&unitId={Guid.NewGuid()}")).StatusCode);
    }

    /// <summary>Два раздела одним запросом и одной фиксацией (D9): материалы пропущены с причиной.</summary>
    [Fact]
    public async Task Массовое_сопоставление_двух_разделов_пропускает_материалы()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var (estimate, revision) = await ImportAsync();
        var editor = await EstimateImportFlow.EditorAsync(host);
        var before = await RateMatchFlow.VersionAsync(editor, estimate, revision);

        var response = await editor.SendAsync(LinesMatch(revision, before,
            new { sections = new[] { "1", "4" }, workTypeId = c.WorkType, unitId = c.Piece, sourceUnit = "100 шт", remember = false }));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var outcome = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rows = await RateMatchFlow.RowsAsync(editor, revision);
        var inSections = rows.Where(r => r.GetProperty("lsrSection").GetProperty("code").GetString() is "1" or "4").ToList();

        var matched = inSections.Where(r => r.GetProperty("workTypeId").ValueKind == JsonValueKind.String).ToList();
        Assert.Equal(matched.Count, outcome.GetProperty("matched").GetInt32());
        Assert.True(matched.Count > 0);
        Assert.All(matched, r => Assert.Equal("work", r.GetProperty("kind").GetString()));
        Assert.Contains(matched, r => r.GetProperty("lsrSection").GetProperty("code").GetString() == "4");
        var materials = inSections.Where(r => r.GetProperty("kind").GetString() == "material").Select(r => r.GetProperty("id").GetGuid()).Order();
        Assert.Equal(materials, outcome.GetProperty("skipped").EnumerateArray()
            .Where(s => s.GetProperty("reason").GetString() == "material").Select(s => s.GetProperty("rowId").GetGuid()).Order());
        Assert.DoesNotContain(rows.Except(inSections), r => r.GetProperty("workTypeId").ValueKind == JsonValueKind.String
            && r.GetProperty("kind").GetString() == "work" && !RateMatchFlow.Marks(r).Contains("remembered"));

        var version = outcome.GetProperty("version").GetString();
        Assert.NotEqual(before, version);
        Assert.Equal(await RateMatchFlow.VersionAsync(editor, estimate, revision), version);
    }

    /// <summary>
    /// Выбранные строки (D9): без версии — 400, по устаревшей — 409 без единой правки; сверх 1000 строк — 400 с
    /// числом; с одним правом чтения — 403.
    /// </summary>
    [Fact]
    public async Task Массовое_сопоставление_строк_версия_предел_и_права()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var (estimate, revision) = await ImportAsync();
        var editor = await EstimateImportFlow.EditorAsync(host);
        var work = (await RateMatchFlow.RowsAsync(editor, revision))
            .Where(r => r.GetProperty("kind").GetString() == "work" && RateMatchFlow.Marks(r).Contains("unmatched"))
            .Select(r => r.GetProperty("id").GetGuid()).ToList();
        var chosen = work.Take(2).ToArray();
        object Body(IEnumerable<Guid> ids) => new { lineIds = ids, workTypeId = c.WorkType, unitId = c.Piece, factor = 1m, remember = false };

        var stale = await RateMatchFlow.VersionAsync(editor, estimate, revision);
        Assert.Equal(HttpStatusCode.OK, (await editor.SendAsync(RateMatchFlow.MatchRow(revision, work[^1], stale,
            new { workTypeId = c.WorkType, unitId = c.Piece, factor = 1m, remember = false }))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await editor.SendAsync(LinesMatch(revision, null, Body(chosen)))).StatusCode);
        var conflict = await editor.SendAsync(LinesMatch(revision, stale, Body(chosen)));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.All(chosen, id => Assert.Null(RateMatchFlow.Stored(host, id).WorkTypeId));

        var tooMany = await editor.SendAsync(LinesMatch(revision, await RateMatchFlow.VersionAsync(editor, estimate, revision),
            Body(Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()))));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Contains("1001", (await tooMany.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(
            LinesMatch(revision, await RateMatchFlow.VersionAsync(editor, estimate, revision), Body(chosen)))).StatusCode);

        var done = await editor.SendAsync(LinesMatch(revision, await RateMatchFlow.VersionAsync(editor, estimate, revision), Body(chosen)));
        Assert.True(done.StatusCode == HttpStatusCode.OK, await done.Content.ReadAsStringAsync());
        Assert.Equal(2, (await done.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("matched").GetInt32());
        Assert.All(chosen, id => Assert.Equal(c.WorkType, RateMatchFlow.Stored(host, id).WorkTypeId));
    }

    /// <summary>
    /// Материалы по материалу (D10): все ресурсы самой частой группы одним запросом; без версии — 400, по устаревшей —
    /// 409 без правки, с одним правом чтения — 403; цена — только с правом на себестоимость.
    /// </summary>
    [Fact]
    public async Task Материалы_по_материалу_одной_фиксацией()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var (estimate, revision) = await ImportAsync();
        var editor = await EstimateImportFlow.EditorAsync(host);

        var list = await editor.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{revision}/materials");
        Assert.Equal("cost", Assert.Single(list.GetProperty("hidden").EnumerateArray()).GetProperty("code").GetString());
        var groups = list.GetProperty("items").EnumerateArray().ToList();
        Assert.All(groups, g => Assert.False(g.TryGetProperty("cost", out _)));
        var biggest = groups.MaxBy(g => g.GetProperty("rows").GetInt32());
        var key = biggest.GetProperty("key").GetString()!;
        var resources = await ResourcesAsync(revision, biggest);
        Assert.True(resources.Count >= 14, $"Самый частый материал ЭОМ стоит лишь в {resources.Count} строках.");
        Assert.Equal(resources.Sum(r => r.Quantity), biggest.GetProperty("quantity").GetDecimal());

        var stale = await RateMatchFlow.VersionAsync(editor, estimate, revision);
        var other = groups.First(g => g.GetProperty("key").GetString() != key).GetProperty("key").GetString();
        Assert.Equal(HttpStatusCode.OK, (await editor.SendAsync(MaterialsMatch(revision, stale,
            new { key = other, nomenclatureId = c.Nomenclature, remember = false }))).StatusCode);

        var body = new { key, nomenclatureId = c.Nomenclature, remember = false };
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.SendAsync(MaterialsMatch(revision, null, body))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await editor.SendAsync(MaterialsMatch(revision, stale, body))).StatusCode);
        Assert.All(await ResourcesAsync(revision, biggest), r => Assert.Null(r.NomenclatureId));
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(
            MaterialsMatch(revision, await RateMatchFlow.VersionAsync(editor, estimate, revision), body))).StatusCode);

        var done = await editor.SendAsync(MaterialsMatch(revision, await RateMatchFlow.VersionAsync(editor, estimate, revision), body));
        Assert.True(done.StatusCode == HttpStatusCode.OK, await done.Content.ReadAsStringAsync());
        var outcome = await done.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(resources.Count, outcome.GetProperty("resources").GetInt32());
        Assert.Equal(resources.Count, outcome.GetProperty("matched").GetInt32());
        Assert.All(await ResourcesAsync(revision, biggest), r => Assert.Equal(c.Nomenclature, r.NomenclatureId));
        var after = (await editor.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{revision}/materials"))
            .GetProperty("items").EnumerateArray().Single(g => g.GetProperty("key").GetString() == key);
        Assert.Equal(c.Nomenclature, after.GetProperty("nomenclatureId").GetGuid());
        Assert.Empty(after.GetProperty("marks").EnumerateArray());

        var estimator = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, PlanModule.EstimateCost);
        var priced = await estimator.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{revision}/materials");
        Assert.False(priced.TryGetProperty("hidden", out _));
        Assert.All(priced.GetProperty("items").EnumerateArray(), g => Assert.True(g.TryGetProperty("cost", out _)));
    }

    /// <summary>«Инженер ИД» модуля не имеет: каждый новый адрес — отказ ворот (ТЗ AUTH-8).</summary>
    [Fact]
    public async Task Инженер_ИД_получает_отказ_ворот_на_новых_адресах()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = (await EstimateSeed.RevisionAsync(host, estimate, new EstimateRowDraft("Строка"))).Id;
        var engineer = await AccessProbe.SignInAsync(host, SystemRoles.IdEngineer, "plan31");

        HttpRequestMessage[] requests =
        [
            new(HttpMethod.Get, "/api/plan/estimates"),
            new(HttpMethod.Get, $"/api/plan/revisions/{revision}/sections"),
            new(HttpMethod.Get, $"/api/plan/revisions/{revision}/materials"),
            new(HttpMethod.Get, $"/api/plan/estimates/{estimate}/current-revision/preview?revisionId={revision}"),
            new(HttpMethod.Get, $"/api/plan/unit-factor?sourceUnit=м&unitId={c.Unit}"),
            LinesMatch(revision, "1", new { sections = new[] { "1" }, workTypeId = c.WorkType }),
            MaterialsMatch(revision, "1", new { key = "x", nomenclatureId = c.Nomenclature }),
        ];
        foreach (var request in requests)
            Assert.Equal(HttpStatusCode.Forbidden, (await engineer.SendAsync(request)).StatusCode);
    }

    private async Task<(Guid Estimate, Guid Revision)> ImportAsync()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var editor = await EstimateImportFlow.EditorAsync(host);
        return await RateMatchFlow.ImportAsync(editor, site.Construction, GrandSmetaSamples.Gsfx(Eom));
    }

    private static HttpRequestMessage LinesMatch(Guid revision, string? version, object body) =>
        RateMatchFlow.With(HttpMethod.Post, $"/api/plan/revisions/{revision}/lines/match", version, body);

    private static HttpRequestMessage MaterialsMatch(Guid revision, string? version, object body) =>
        RateMatchFlow.With(HttpMethod.Put, $"/api/plan/revisions/{revision}/materials/match", version, body);

    /// <summary>Ресурсы группы, как они лежат: код, наименование и единица — как у группы в ответе.</summary>
    private async Task<List<EstimateRowMaterial>> ResourcesAsync(Guid revision, JsonElement group)
    {
        string? Text(string name) => group.GetProperty(name).ValueKind == JsonValueKind.Null ? null : group.GetProperty(name).GetString();
        var (code, name, unit) = (Text("sourceCode"), Text("sourceName"), Text("sourceUnit"));
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        return await (from m in db.EstimateRowMaterials.AsNoTracking()
                      join r in db.EstimateRows on m.RowId equals r.Id
                      where r.RevisionId == revision && m.SourceCode == code && m.SourceName == name && m.SourceUnit == unit
                      select m).ToListAsync();
    }
}
