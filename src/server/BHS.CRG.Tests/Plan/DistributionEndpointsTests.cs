using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Plan.DistributionFlow;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Адреса распределения редакции (change <c>plan-10-distribution</c>, задачи 1.3, 1.4, 1.6, 1.7, 1.9; ТЗ PLAN-5.1,
/// PLAN-5, AUTH-8, AUTH-9, CORE-28, CORE-34.1): правило раздела ЛСР, набор строк, версия и права, счётчики в ответах
/// смет и разделов ЛСР, журнал, зафиксированная редакция, смета на разделе и раздел стройки как держатель.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class DistributionEndpointsTests(PlanOnlyHost host)
{
    private sealed record Seeded(EstimateSeed.Site Site, Guid Second, Guid Estimate, Guid Revision, List<Guid> Rows);

    /// <summary>
    /// Редакция: раздел 1 «Кабельные линии» — сопоставленная строка «12 · 100 м» и две простые; раздел 2 «Освещение»
    /// — строка без объёма. Стройка с двумя разделами.
    /// </summary>
    private async Task<Seeded> SeedAsync()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var second = await SectionAsync(host, site.Construction, "ЭОМ-2");
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var matched = await EstimateSeed.MatchedRowAsync(host, site, 1200m) with
        {
            LsrSectionCode = "1", LsrSectionName = "Кабельные линии", NumberInSection = "1", Number = "1",
        };
        var revision = await EstimateSeed.RevisionAsync(host, estimate, matched,
            Row("1", "Кабельные линии", "ГЭСНм08-02-148-01", 3m, "2"),
            Row("1", "Кабельные линии", "ГЭСНм08-03-591-02", 5m, "3"),
            Row("2", "Освещение", "ПНР-01", null, "4"));
        return new Seeded(site, second, estimate, revision.Id, await RowIdsAsync(host, revision.Id));
    }

    [Fact]
    public async Task Правило_раздела_меняет_счётчики_редакции_и_раздела_стройка_в_целом_не_нераспределена()
    {
        var s = await SeedAsync();
        var editor = await EditorAsync(host);
        var before = await CountsAsync(editor, s.Estimate, s.Revision);
        Assert.Equal(4, before.GetProperty("rows").GetInt32());
        Assert.Equal(4, before.GetProperty("undistributed").GetInt32());

        await RuleAsync(editor, s.Revision, "1", Section(s.Site.Section));
        var counts = await CountsAsync(editor, s.Estimate, s.Revision);
        Assert.Equal(3, counts.GetProperty("distributed").GetInt32());
        Assert.Equal(1, counts.GetProperty("undistributed").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, counts.GetProperty("changedAt").ValueKind);
        var sections = (await editor.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{s.Revision}/sections"))
            .GetProperty("items").EnumerateArray().ToList();
        var first = Assert.Single(sections, x => x.GetProperty("code").GetString() == "1");
        Assert.Equal("section", first.GetProperty("rule").GetProperty("kind").GetString());
        Assert.Equal(0, first.GetProperty("undistributed").GetInt32());
        Assert.Equal(1, Assert.Single(sections, x => x.GetProperty("code").GetString() == "2").GetProperty("undistributed").GetInt32());

        // Строка без объёма под правилом «стройка в целом» — решение, а не «не распределено».
        var view = (await RuleAsync(editor, s.Revision, "2", Whole)).GetProperty("distribution");
        var whole = await CountsAsync(editor, s.Estimate, s.Revision);
        Assert.Equal(0, whole.GetProperty("undistributed").GetInt32());
        Assert.Equal(1, whole.GetProperty("whole").GetInt32());
        var line = LineOf(view, s.Rows[3]);
        Assert.False(line.GetProperty("splittable").GetBoolean());
        Assert.Equal("no-volume", line.GetProperty("splitReason").GetString());
        Assert.Equal("whole", line.GetProperty("status").GetString());
        // Счётчики ответа распределения — те же, что у сметы.
        Assert.Equal(whole.GetProperty("distributed").GetInt32(), view.GetProperty("counts").GetProperty("distributed").GetInt32());
        Assert.Equal(3, Assert.Single(view.GetProperty("targets").GetProperty("sections").EnumerateArray(),
            t => t.GetProperty("sectionId").GetGuid() == s.Site.Section).GetProperty("rows").GetInt32());
    }

    [Fact]
    public async Task Части_в_единице_плана_и_отказ_сверх_объёма_в_обеих_единицах()
    {
        var s = await SeedAsync();
        var editor = await EditorAsync(host);

        var view = (await LinesAsync(editor, s.Revision,
            Line(s.Rows[0], "plan", (s.Site.Section, 700m), (s.Second, 500m)))).GetProperty("distribution");
        var line = LineOf(view, s.Rows[0]);
        Assert.Equal("parts", line.GetProperty("source").GetString());
        Assert.Equal("distributed", line.GetProperty("status").GetString());
        var parts = line.GetProperty("parts").EnumerateArray().ToList();
        Assert.Equal([7m, 5m], parts.Select(p => p.GetProperty("quantity").GetProperty("row").GetDecimal()));
        Assert.Equal([700m, 500m], parts.Select(p => p.GetProperty("quantity").GetProperty("plan").GetDecimal()));
        Assert.Equal(0m, line.GetProperty("rest").GetProperty("row").GetDecimal());

        var version = await VersionAsync(editor, s.Revision);
        var over = await editor.SendAsync(Lines(s.Revision, version,
            new { lines = new[] { Line(s.Rows[0], "row", (s.Site.Section, 7m), (s.Second, 6m)) } }));
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        var error = Assert.Single((await over.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors").EnumerateArray());
        Assert.Equal(s.Rows[0], error.GetProperty("rowId").GetGuid());
        Assert.Equal(13m, error.GetProperty("sum").GetProperty("row").GetDecimal());
        Assert.Equal(1300m, error.GetProperty("sum").GetProperty("plan").GetDecimal());
        Assert.Equal(1200m, error.GetProperty("volume").GetProperty("plan").GetDecimal());

        // Остаток — «стройка в целом», строка — частично.
        var partial = LineOf((await LinesAsync(editor, s.Revision, Line(s.Rows[0], "plan", (s.Site.Section, 700m))))
            .GetProperty("distribution"), s.Rows[0]);
        Assert.Equal("partial", partial.GetProperty("status").GetString());
        Assert.Equal(500m, partial.GetProperty("rest").GetProperty("plan").GetDecimal());
        Assert.True(partial.GetProperty("whole").GetBoolean());
    }

    [Fact]
    public async Task Набор_с_одной_нарушившей_строкой_не_пишет_ничего_и_называет_её()
    {
        var s = await SeedAsync();
        var editor = await EditorAsync(host);
        var version = await VersionAsync(editor, s.Revision);

        var response = await editor.SendAsync(Lines(s.Revision, version, new
        {
            lines = new[]
            {
                Line(s.Rows[1], "row", (s.Site.Section, 3m)), Line(s.Rows[2], "row", (s.Second, 9m)),
                Line(s.Rows[3], "row", (s.Second, 1m)),
            },
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("balance", body.GetProperty("code").GetString());
        Assert.Equal([(s.Rows[2], "over"), (s.Rows[3], "no-volume")], body.GetProperty("errors").EnumerateArray()
            .Select(e => (e.GetProperty("rowId").GetGuid(), e.GetProperty("code").GetString())));

        var after = await GetAsync(editor, s.Revision);
        Assert.Equal(version, after.GetProperty("version").GetString());
        Assert.Equal("none", LineOf(after, s.Rows[1]).GetProperty("source").GetString());
    }

    [Fact]
    public async Task Без_версии_400_устаревшая_409_с_версией_автором_и_моментом()
    {
        var s = await SeedAsync();
        var editor = await EditorAsync(host);
        var body = new { lsrSectionCode = "1", target = Section(s.Site.Section) };

        Assert.Equal(HttpStatusCode.BadRequest, (await editor.SendAsync(Rules(s.Revision, null, body))).StatusCode);
        var first = await VersionAsync(editor, s.Revision);
        Assert.Equal("0", first);
        await OkAsync(editor, Rules(s.Revision, first, body));

        var stale = await editor.SendAsync(Rules(s.Revision, first, new { lsrSectionCode = "1", target = Whole }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var conflict = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("distribution-changed", conflict.GetProperty("code").GetString());
        Assert.Equal(await VersionAsync(editor, s.Revision), conflict.GetProperty("version").GetString());
        Assert.False(string.IsNullOrEmpty(conflict.GetProperty("changedBy").GetString()));
        Assert.NotEqual(JsonValueKind.Null, conflict.GetProperty("changedAt").ValueKind);
        // Отказ ничего не записал.
        Assert.Equal("section", RuleOf(await GetAsync(editor, s.Revision), "1").GetProperty("target").GetProperty("kind").GetString());
    }

    /// <summary>
    /// Одновременные записи одной версией: та же часть и то же правило. Проигравшая упирается в xmin заголовка или в
    /// единственность части и правила — какой отказ придёт первым, не определено, и каждый обязан быть 409.
    /// </summary>
    [Fact]
    public async Task Одновременная_запись_той_же_части_и_правила_одна_проходит_вторая_409()
    {
        var editor = await EditorAsync(host);
        for (var round = 0; round < 4; round++)
        {
            var s = await SeedAsync();
            // Чётный круг — первая запись (заголовка нет), нечётный — по существующему заголовку.
            if (round % 2 == 1) await RuleAsync(editor, s.Revision, "1", Section(s.Site.Section));
            var version = await VersionAsync(editor, s.Revision);
            var line = new { lines = new[] { Line(s.Rows[0], "row", (s.Site.Section, 1m)) } };
            var rule = new { lsrSectionCode = "1", target = Whole };
            var answers = await Task.WhenAll(
                editor.SendAsync(Lines(s.Revision, version, line)), editor.SendAsync(Lines(s.Revision, version, line)),
                editor.SendAsync(Rules(s.Revision, version, rule)), editor.SendAsync(Rules(s.Revision, version, rule)));
            var codes = answers.Select(a => a.StatusCode).ToList();
            Assert.True(codes.All(c => c is HttpStatusCode.OK or HttpStatusCode.Conflict),
                string.Join(", ", codes) + ": " + string.Join(" | ", await Task.WhenAll(answers.Select(a => a.Content.ReadAsStringAsync()))));
            Assert.Single(codes, HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task С_одним_чтением_403_а_правила_и_части_видны()
    {
        var s = await SeedAsync();
        var editor = await EditorAsync(host);
        await RuleAsync(editor, s.Revision, "1", Section(s.Site.Section));
        await LinesAsync(editor, s.Revision, Line(s.Rows[1], "row", (s.Second, 3m)));
        var reader = await ReaderAsync(host);
        var version = await VersionAsync(reader, s.Revision);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.SendAsync(Rules(s.Revision, version, new { lsrSectionCode = "1", target = Whole }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await reader.SendAsync(Lines(s.Revision, version, new { lines = new[] { ToRule(s.Rows[1]) } }))).StatusCode);
        var seen = await GetAsync(reader, s.Revision);
        Assert.Equal("section", RuleOf(seen, "1").GetProperty("target").GetProperty("kind").GetString());
        Assert.Equal(s.Second, Assert.Single(LineOf(seen, s.Rows[1]).GetProperty("parts").EnumerateArray()).GetProperty("sectionId").GetGuid());
    }

    [Fact]
    public async Task Зафиксированная_редакция_правится_набор_из_шести_строк_одна_запись_журнала()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = (await EstimateSeed.RevisionAsync(host, estimate,
            [.. Enumerable.Range(1, 6).Select(i => Row("1", "Кабельные линии", $"ГЭСН-{i}", 2m, i.ToString()))])).Id;
        var rows = await RowIdsAsync(host, revision);
        await FreezeAsync(host, revision);
        var editor = await EditorAsync(host);

        var written = await LinesAsync(editor, revision, [.. rows.Select(r => Line(r, "row", (site.Section, 1m)))]);
        Assert.True(written.GetProperty("distribution").GetProperty("frozen").GetBoolean());
        // Прежнее состояние — «по правилу» у каждой из шести: им набор и отменяют.
        var previous = written.GetProperty("previous").GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(6, previous.Count);
        Assert.All(previous, l => Assert.Equal(JsonValueKind.Null, l.GetProperty("parts").ValueKind));
        var record = Assert.Single(await JournalAsync(host, revision, "plan.distribution.lines"));
        Assert.Contains("строка 6:", record.After);
        Assert.Contains("по правилу раздела", record.Before);

        var undone = await LinesAsync(editor, revision, [.. previous.Select(l => (object)l)]);
        Assert.All(undone.GetProperty("distribution").GetProperty("lines").EnumerateArray(),
            l => Assert.Equal(0, l.GetProperty("parts").GetArrayLength()));
        // Пустой набор — 400: он ничего не меняет, а версию сдвинул бы.
        Assert.Equal(HttpStatusCode.BadRequest, (await editor.SendAsync(
            Lines(revision, await VersionAsync(editor, revision), new { lines = Array.Empty<object>() }))).StatusCode);
        Assert.NotEqual(JsonValueKind.Null,
            (await CountsAsync(editor, estimate, revision)).GetProperty("changedAt").ValueKind);
    }

    [Fact]
    public async Task Правило_без_раздела_или_с_неизвестным_разделом_400()
    {
        var s = await SeedAsync();
        var editor = await EditorAsync(host);
        var version = await VersionAsync(editor, s.Revision);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await editor.SendAsync(Rules(s.Revision, version, new { target = Whole }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await editor.SendAsync(Rules(s.Revision, version, new { lsrSectionCode = "99", target = Whole }))).StatusCode);
        // Строк вне разделов в редакции нет — и группы null нет.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await editor.SendAsync(Rules(s.Revision, version, new { lsrSectionCode = (string?)null, target = Whole }))).StatusCode);
        Assert.Equal(version, await VersionAsync(editor, s.Revision));
        Assert.All((await GetAsync(editor, s.Revision)).GetProperty("rules").EnumerateArray(),
            r => Assert.Equal(JsonValueKind.Null, r.GetProperty("target").ValueKind));
    }

    [Fact]
    public async Task Сброс_исключений_и_отмена_одной_записью_возвращают_правило_и_части_одной_версией()
    {
        var s = await SeedAsync();
        var editor = await EditorAsync(host);
        await RuleAsync(editor, s.Revision, "1", Section(s.Site.Section));
        await LinesAsync(editor, s.Revision, Line(s.Rows[1], "row", (s.Second, 2m)));

        var reset = await RuleAsync(editor, s.Revision, "1", Section(s.Second), reset: true);
        var previous = reset.GetProperty("previous");
        Assert.Equal(s.Site.Section, previous.GetProperty("rule").GetProperty("target").GetProperty("sectionId").GetGuid());
        Assert.Equal("rule", LineOf(reset.GetProperty("distribution"), s.Rows[1]).GetProperty("source").GetString());
        Assert.Contains("сняты части строк 2", Assert.Single(await JournalAsync(host, s.Revision, "plan.distribution.rule"),
            r => r.After!.Contains("сняты")).After);

        var undone = await OkAsync(editor, Rules(s.Revision, reset.GetProperty("version").GetString(), new
        {
            lsrSectionCode = "1",
            target = previous.GetProperty("rule").GetProperty("target"),
            lines = previous.GetProperty("lines"),
        }));
        var view = undone.GetProperty("distribution");
        Assert.Equal(s.Site.Section, RuleOf(view, "1").GetProperty("target").GetProperty("sectionId").GetGuid());
        var restored = Assert.Single(LineOf(view, s.Rows[1]).GetProperty("parts").EnumerateArray());
        Assert.Equal(s.Second, restored.GetProperty("sectionId").GetGuid());
        Assert.Equal(2m, restored.GetProperty("quantity").GetProperty("row").GetDecimal());
        Assert.NotEqual(reset.GetProperty("version").GetString(), undone.GetProperty("version").GetString());
    }

    [Fact]
    public async Task Смета_на_разделе_распределения_нет_правка_400()
    {
        var site = await EstimateSeed.SiteAsync(host);
        Guid estimate;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            var onSection = Estimate.Create(site.Construction, site.Section, "На раздел", EstimateKind.Main, DateTimeOffset.UtcNow);
            db.Estimates.Add(onSection);
            await db.SaveChangesAsync();
            estimate = onSection.Id;
        }
        var revision = (await EstimateSeed.RevisionAsync(host, estimate, Row("1", "Кабельные линии", "ГЭСН-1", 2m))).Id;
        var editor = await EditorAsync(host);

        var view = await GetAsync(editor, revision);
        Assert.False(view.GetProperty("available").GetBoolean());
        Assert.Equal("estimate-section", view.GetProperty("reason").GetProperty("code").GetString());
        Assert.Equal("ЭОМ-1", view.GetProperty("reason").GetProperty("sectionName").GetString());
        var refused = await editor.SendAsync(Rules(revision, view.GetProperty("version").GetString(),
            new { lsrSectionCode = "1", target = Whole }));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("estimate-section", body.GetProperty("code").GetString());
        Assert.Equal(site.Section, body.GetProperty("sectionId").GetGuid());
        Assert.Equal(1, (await CountsAsync(editor, estimate, revision)).GetProperty("distributed").GetInt32());
    }

    [Fact]
    public async Task Занятый_раздел_не_удалить_отказ_называет_строки_и_редакцию()
    {
        var s = await SeedAsync();
        var editor = await EditorAsync(host);
        await LinesAsync(editor, s.Revision, Line(s.Rows[1], "row", (s.Second, 3m)), Line(s.Rows[2], "row", (s.Second, 5m)));
        var admin = await GrantedSignIn.WithPermissionsAsync(host,
            CorePermissions.ConstructionsRead, CorePermissions.ConstructionsEdit, BHS.CRG.Modules.Plan.PlanModule.EstimateRead);

        var refused = await admin.DeleteAsync($"/api/sections/{s.Second}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var text = await refused.Content.ReadAsStringAsync();
        Assert.Contains("строки смет, части которых распределены на этот раздел — 2", text);
        Assert.Contains("ред. 1", text);
    }

    [Fact]
    public async Task Цель_которой_в_ядре_нет_deleted_в_итоге_по_целям()
    {
        var s = await SeedAsync();
        var editor = await EditorAsync(host);
        await RuleAsync(editor, s.Revision, "1", Section(s.Site.Section));
        var gone = Guid.NewGuid();
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            // Раздел пропал из ядра в обход держателей — распределение на него отвечает целью deleted, а не пустотой.
            var rule = db.DistributionRules.Single(r => r.RevisionId == s.Revision);
            db.Entry(rule).Property(r => r.SectionId).CurrentValue = gone;
            await db.SaveChangesAsync();
        }

        var target = Assert.Single((await GetAsync(editor, s.Revision)).GetProperty("targets").GetProperty("sections")
            .EnumerateArray(), t => t.GetProperty("sectionId").GetGuid() == gone);
        Assert.True(target.GetProperty("deleted").GetBoolean());
        Assert.Equal(3, target.GetProperty("rows").GetInt32());
    }
}

/// <summary>На умолчательном хосте модуль <c>plan</c> выключен: адреса распределения — отказ выключенного модуля (501).</summary>
[Collection("Integration")]
public class DistributionWithoutModuleTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Модуль_выключен_501()
    {
        var client = fixture.CreateClient();
        var revision = Guid.NewGuid();
        foreach (var request in new[]
                 {
                     new HttpRequestMessage(HttpMethod.Get, $"/api/plan/revisions/{revision}/distribution"),
                     Rules(revision, "0", new { lsrSectionCode = "1", target = Whole }),
                     Lines(revision, "0", new { lines = Array.Empty<object>() }),
                     Decide(revision, Guid.NewGuid(), "0", new { action = "skip" }),
                 })
        {
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
            Assert.Equal("plan", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("module").GetString());
        }
    }
}
