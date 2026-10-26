using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Носители экранов смет — строка, редакция, импорт (change <c>plan-31-estimate-ui-carriers</c>, PR 1; ТЗ PLAN-4,
/// PLAN-7, PLAN-6.1, PLAN-9.1): номер позиции и подчинённость, строка-материал, неактивная позиция, счётчики и
/// импорт редакции.
///
/// <para>⚠️ Запомненное общее на базу хоста (<see cref="RateMatchFlow" />): счётчики сверяются с пометками строк
/// того же ответа, а не с числами файла, — знакомый шифр образца, запомненный другим тестом, их бы сдвинул.</para>
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class EstimateCarriersImportTests(PlanOnlyHost host)
{
    private const string Eom = "lsr-16.2.2-eom";

    [Fact]
    public async Task Строки_несут_номер_файла_подчинённость_и_вид()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var editor = await EstimateImportFlow.EditorAsync(host);
        var (estimate, revision) = await ImportAsync(editor, site.Construction, Eom);
        var rows = await RateMatchFlow.RowsAsync(editor, revision);
        Assert.Equal(238, rows.Count);

        var work = Assert.Single(rows, r => r.GetProperty("number").GetString() == "13");
        Assert.False(work.GetProperty("subordinate").GetBoolean());
        Assert.Equal("work", work.GetProperty("kind").GetString());
        var slave = Assert.Single(rows, r => r.GetProperty("number").GetString() == "13.1");
        Assert.True(slave.GetProperty("subordinate").GetBoolean());
        Assert.Equal("material", slave.GetProperty("kind").GetString());
        // Номер в разделе — порядковый, ключ переноса (D9 зонтика); номер файла его не заменяет.
        Assert.NotEqual("13.1", slave.GetProperty("numberInSection").GetString());

        Assert.Equal(145, rows.Count(r => r.GetProperty("kind").GetString() == "material"));
        await CountersMatchRowsAsync(editor, estimate, revision, rows);
    }

    /// <summary>
    /// Строка-материал (D2): без пометки «не сопоставлена», вне счётчика, с материалом из самой позиции; вид работы
    /// ей — 400, а раздел ЛСР сопоставляется без неё.
    /// </summary>
    [Fact]
    public async Task Строка_материал_без_unmatched_со_своим_материалом_и_отказом_виду_работы()
    {
        var c = await EstimateSeed.ClassifierAsync(host);
        var site = await EstimateSeed.SiteAsync(host);
        var editor = await EstimateImportFlow.EditorAsync(host);
        var (estimate, revision) = await ImportAsync(editor, site.Construction, Eom);
        var rows = await RateMatchFlow.RowsAsync(editor, revision);

        var material = Assert.Single(rows, r => r.GetProperty("number").GetString() == "13.1");
        Assert.DoesNotContain("unmatched", RateMatchFlow.Marks(material));
        var own = Assert.Single(material.GetProperty("materials").EnumerateArray());
        Assert.Equal(material.GetProperty("name").GetString(), own.GetProperty("sourceName").GetString());
        // Шифр ТЦ_ — строка КА, он и есть код материала.
        Assert.StartsWith("ТЦ_", own.GetProperty("sourceCode").GetString());
        Assert.Equal(material.GetProperty("rateCode").GetString(), own.GetProperty("sourceCode").GetString());
        Assert.Equal(material.GetProperty("sourceUnit").GetString(), own.GetProperty("sourceUnit").GetString());
        Assert.Equal(material.GetProperty("sourceVolume").GetDecimal(), own.GetProperty("quantity").GetDecimal());
        Assert.All(rows.Where(r => r.GetProperty("kind").GetString() == "material"),
            r => Assert.DoesNotContain("unmatched", RateMatchFlow.Marks(r)));

        var materialId = material.GetProperty("id").GetGuid();
        var refused = await editor.SendAsync(RateMatchFlow.MatchRow(revision, materialId,
            await RateMatchFlow.VersionAsync(editor, estimate, revision), new { workTypeId = c.WorkType, unitId = c.Piece, remember = false }));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("материал, а не работа", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Null(RateMatchFlow.Stored(host, materialId).WorkTypeId);

        // Раздел 1 — работы в «шт» вперемешку с материалами: материалы пропущены с причиной и не тронуты.
        var section = rows.Where(r => r.GetProperty("lsrSection").GetProperty("code").GetString() == "1").ToList();
        var materials = section.Where(r => r.GetProperty("kind").GetString() == "material").Select(r => r.GetProperty("id").GetGuid()).ToList();
        Assert.NotEmpty(materials);
        var matched = await editor.SendAsync(RateMatchFlow.MatchSection(revision, await RateMatchFlow.VersionAsync(editor, estimate, revision),
            new { lsrSectionCode = "1", workTypeId = c.WorkType, unitId = c.Piece, sourceUnit = "шт", remember = false }));
        Assert.True(matched.StatusCode == HttpStatusCode.OK, await matched.Content.ReadAsStringAsync());
        var outcome = await matched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(outcome.GetProperty("matched").GetInt32() > 0);
        var skipped = outcome.GetProperty("skipped").EnumerateArray()
            .Where(s => s.GetProperty("reason").GetString() == "material").Select(s => s.GetProperty("rowId").GetGuid()).ToList();
        Assert.Equal(materials.Order(), skipped.Order());
        Assert.All(materials, id => Assert.Null(RateMatchFlow.Stored(host, id).WorkTypeId));

        await CountersMatchRowsAsync(editor, estimate, revision, await RateMatchFlow.RowsAsync(editor, revision));
    }

    /// <summary>
    /// Неактивная позиция (D3) — не строка: отчёт называет её пропущенной, «N из M» она не уменьшает, и редакция
    /// создаётся без согласия на частичный импорт.
    /// </summary>
    [Fact]
    public async Task Неактивная_позиция_пропущена_без_согласия_на_частичный()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var editor = await EstimateImportFlow.EditorAsync(host);
        var report = await EstimateImportFlow.OutcomeAsync(editor,
            await EstimateImportFlow.UploadAsync(editor, site.Construction, "lsr-16.2.2-os-skud"));

        Assert.Equal(75, report.GetProperty("parsed").GetInt32());
        Assert.Equal(75, report.GetProperty("inFile").GetInt32());
        Assert.DoesNotContain("partial", EstimateImportFlow.Marks(report));
        var skipped = Assert.Single(report.GetProperty("skipped").EnumerateArray());
        Assert.Equal("inactive", skipped.GetProperty("reason").GetString());
        Assert.Contains("позиция 13 ГЭСНм08-02-427-01", skipped.GetProperty("place").GetString());
        Assert.Equal("неактивна в файле — пропущена", skipped.GetProperty("text").GetString());
        Assert.Equal(74, report.GetProperty("summary").GetProperty("rows").GetInt32());
        Assert.Equal(74, report.GetProperty("summary").GetProperty("chapters").EnumerateArray().Sum(c => c.GetProperty("rows").GetInt32()));

        var committed = await editor.SendAsync(EstimateImportFlow.Commit(EstimateImportFlow.Id(report), report.GetProperty("version").GetString()));
        Assert.True(committed.StatusCode == HttpStatusCode.Created, await committed.Content.ReadAsStringAsync());
        var done = await committed.Content.ReadFromJsonAsync<JsonElement>();
        var revision = Assert.Single(done.GetProperty("estimate").GetProperty("revisions").EnumerateArray());
        Assert.Equal(74, revision.GetProperty("rows").GetInt32());
        Assert.Equal(JsonValueKind.Null, revision.GetProperty("partial").ValueKind);
        Assert.DoesNotContain(await RateMatchFlow.RowsAsync(editor, done.GetProperty("revisionId").GetGuid()),
            r => r.GetProperty("rateCode").GetString() == "ГЭСНм08-02-427-01");

        // Отчёт у редакции — модель очищена, а пропущенная названа по-прежнему.
        var after = await EstimateImportFlow.GetAsync(editor, EstimateImportFlow.Id(report));
        Assert.Equal("committed", after.GetProperty("state").GetString());
        Assert.Equal(skipped.GetProperty("place").GetString(),
            Assert.Single(after.GetProperty("skipped").EnumerateArray()).GetProperty("place").GetString());
    }

    /// <summary>
    /// Согласие на частичный импорт называет число строк будущей редакции — разобранные без неактивных, а не
    /// «разобрано N» (D3): ОС,СКУД без двух первых позиций — 73 разобрано, 72 строки.
    /// </summary>
    [Fact]
    public async Task Согласие_на_частичный_называет_строки_без_неактивных()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var editor = await EstimateImportFlow.EditorAsync(host);
        var report = await EstimateImportFlow.OutcomeAsync(editor, await EstimateImportFlow.UploadAsync(editor,
            EstimateImportFlow.Form(EstimateImportFlow.WithBrokenPositions("lsr-16.2.2-os-skud", 2), "Data.xml", site.Construction, newName: "Частичная")));
        Assert.Equal(73, report.GetProperty("parsed").GetInt32());

        var refused = await editor.SendAsync(EstimateImportFlow.Commit(EstimateImportFlow.Id(report), report.GetProperty("version").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(73, body.GetProperty("parsed").GetInt32());
        Assert.Equal(72, body.GetProperty("rows").GetInt32());
        Assert.Contains("будет из 72 строк", body.GetProperty("error").GetString());
    }

    /// <summary>Частичная редакция (D4) несёт «N из M» и ссылку на импорт — у читающего с одним <c>plan.estimate.read</c>.</summary>
    [Fact]
    public async Task Частичная_редакция_несёт_числа_и_импорт_читающему()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var editor = await EstimateImportFlow.EditorAsync(host);
        var report = await EstimateImportFlow.OutcomeAsync(editor, await EstimateImportFlow.UploadAsync(editor,
            EstimateImportFlow.Form(EstimateImportFlow.WithBrokenPositions("lsr-16.2.0-vn", 2), "Data.xml", site.Construction, newName: "Частичная")));
        var importId = EstimateImportFlow.Id(report);
        var committed = await editor.SendAsync(EstimateImportFlow.Commit(importId, report.GetProperty("version").GetString(), acceptPartial: true));
        Assert.Equal(HttpStatusCode.Created, committed.StatusCode);
        var estimate = (await committed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("estimate").GetProperty("id").GetGuid();

        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);
        var list = await reader.GetFromJsonAsync<JsonElement>($"/api/plan/estimates?constructionId={site.Construction}");
        var revision = Assert.Single(Assert.Single(list.GetProperty("items").EnumerateArray(), e => e.GetProperty("id").GetGuid() == estimate)
            .GetProperty("revisions").EnumerateArray());
        Assert.Equal(importId, revision.GetProperty("importId").GetGuid());
        Assert.Equal(23, revision.GetProperty("partial").GetProperty("parsed").GetInt32());
        Assert.Equal(25, revision.GetProperty("partial").GetProperty("inFile").GetInt32());
        // Сам отчёт импорта — по-прежнему под правом правки.
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync($"/api/plan/imports/{importId}")).StatusCode);
    }

    /// <summary>Редакция, заведённая не импортом: номера файла нет, строка — работа, импорта и «частично» нет.</summary>
    [Fact]
    public async Task Редакция_не_из_импорта_без_номера_файла_и_импорта()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = await EstimateSeed.RevisionAsync(host, estimate, new EstimateRowDraft("Строка вручную"));
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);

        var row = Assert.Single(await RateMatchFlow.RowsAsync(reader, revision.Id));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("number").ValueKind);
        Assert.False(row.GetProperty("subordinate").GetBoolean());
        Assert.Equal("work", row.GetProperty("kind").GetString());
        var dto = Assert.Single((await reader.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}"))
            .GetProperty("revisions").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("importId").ValueKind);
        Assert.Equal(JsonValueKind.Null, dto.GetProperty("partial").ValueKind);
        Assert.Equal(1, dto.GetProperty("unmatched").GetInt32());
        Assert.Equal(0, dto.GetProperty("materialsUnmatched").GetInt32());
        Assert.Equal(0, dto.GetProperty("unitChanged").GetInt32());
    }

    private static async Task<(Guid Estimate, Guid Revision)> ImportAsync(HttpClient client, Guid construction, string sample) =>
        await RateMatchFlow.ImportAsync(client, construction, GrandSmetaSamples.Gsfx(sample));

    /// <summary>Счётчики редакции считает сервер — и они сходятся с пометками её строк (D4, D17 зонтика).</summary>
    private static async Task CountersMatchRowsAsync(HttpClient client, Guid estimate, Guid revisionId, List<JsonElement> rows)
    {
        var revision = (await client.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}"))
            .GetProperty("revisions").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == revisionId);
        Assert.Equal(rows.Count, revision.GetProperty("rows").GetInt32());
        Assert.Equal(rows.Count(r => RateMatchFlow.Marks(r).Contains("unmatched")), revision.GetProperty("unmatched").GetInt32());
        Assert.Equal(rows.Count(r => RateMatchFlow.Marks(r).Contains("unit-changed")), revision.GetProperty("unitChanged").GetInt32());
        var unmatchedMaterials = rows.SelectMany(r => r.GetProperty("materials").EnumerateArray())
            .Count(m => m.GetProperty("marks").EnumerateArray().Any(x => x.GetProperty("code").GetString() == "unmatched"));
        Assert.True(unmatchedMaterials > 0);
        Assert.Equal(unmatchedMaterials, revision.GetProperty("materialsUnmatched").GetInt32());
        Assert.Equal(JsonValueKind.String, revision.GetProperty("importId").ValueKind);
    }
}
