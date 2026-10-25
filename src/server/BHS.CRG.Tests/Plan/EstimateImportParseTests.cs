using System.Text.Json;
using BHS.CRG.Domain.Jobs;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.GrandSmeta;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Загрузка файла и отчёт разбора (change <c>plan-07-estimate-import</c>, PR 1; ТЗ PLAN-6, PLAN-7, STG-12): файл
/// ложится в хранилище модуля, разбор (<c>plan-05</c>) идёт фоновой операцией, отчёт называет вид, версию схемы,
/// «N из M», не разобранное с причиной и сводку будущей редакции.
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class EstimateImportParseTests(PlanOnlyHost host)
{
    private const string Lsr = "lsr-16.2.0-vn";

    /// <summary>
    /// Ответ загрузки — импорт, ещё не созданная редакция; разбор поставлен операцией <c>plan.import.parse</c> с
    /// целью «запись импорта». Разобранная ЛСР — отчёт с видом, версией схемы и сводкой.
    /// </summary>
    [Fact]
    public async Task ЛСР_загружается_и_разбирается_в_отчёт()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.CostEditorAsync(host);

        var uploaded = await EstimateImportFlow.UploadAsync(client, site.Construction, Lsr);
        // Разбор идёт в фоне: ответ уже может застать его законченным, но редакции нет ни в одном случае.
        Assert.Contains(uploaded.GetProperty("state").GetString(), new[] { "parsing", "parsed" });
        Assert.Equal(JsonValueKind.Null, uploaded.GetProperty("revisionId").ValueKind);
        Assert.Equal("Из файла", uploaded.GetProperty("target").GetProperty("newEstimate").GetProperty("name").GetString());
        using (var scope = host.Services.CreateScope())
        {
            var job = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.AsNoTracking()
                .SingleAsync(j => j.TargetId == EstimateImportFlow.Id(uploaded));
            Assert.Equal(JobKind.ModuleWork, job.Kind);
            Assert.Contains("plan.import.parse", job.Payload);
        }

        var report = await EstimateImportFlow.OutcomeAsync(client, uploaded);
        var expected = LocalEstimateReader.Read(GrandSmetaSamples.Open(Lsr));
        Assert.Equal("parsed", report.GetProperty("state").GetString());
        Assert.Equal("lsr", report.GetProperty("kind").GetString());
        Assert.Equal(GrandSmetaSchemas.Edition, report.GetProperty("schemaVersion").GetString());
        Assert.StartsWith("16.2.0", report.GetProperty("programVersion").GetString());
        Assert.Equal(expected.PositionsInFile, report.GetProperty("parsed").GetInt32());
        Assert.Equal(expected.PositionsInFile, report.GetProperty("inFile").GetInt32());
        Assert.Empty(report.GetProperty("unparsed").EnumerateArray());
        Assert.DoesNotContain("partial", EstimateImportFlow.Marks(report));

        var summary = report.GetProperty("summary");
        Assert.Equal(expected.Positions.Count(), summary.GetProperty("rows").GetInt32());
        Assert.Equal(expected.Chapters.Count, summary.GetProperty("chapters").GetArrayLength());
        Assert.Equal(expected.Requisites.Number, summary.GetProperty("lsrNumber").GetString());
        Assert.Equal(expected.Totals.Total, summary.GetProperty("cost").GetProperty("total").GetDecimal());
        Assert.Equal(expected.Chapters[0].Total,
            summary.GetProperty("chapters")[0].GetProperty("cost").GetProperty("total").GetDecimal());
        Assert.False(report.TryGetProperty("hidden", out _));
    }

    /// <summary>Частичный файл: «разобрано N из M» и каждое не разобранное — с местом и причиной (ТЗ PLAN-7).</summary>
    [Fact]
    public async Task Частичный_файл_отчёт_перечисляет_не_разобранные()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.EditorAsync(host);
        var all = LocalEstimateReader.Read(GrandSmetaSamples.Open(Lsr)).PositionsInFile;

        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form(EstimateImportFlow.WithBrokenPositions(Lsr, 3), "Data.xml", site.Construction, newName: "Частичная")));

        Assert.Equal("parsed", report.GetProperty("state").GetString());
        Assert.Equal(all - 3, report.GetProperty("parsed").GetInt32());
        Assert.Equal(all, report.GetProperty("inFile").GetInt32());
        var unparsed = report.GetProperty("unparsed").EnumerateArray().ToList();
        Assert.Equal(3, unparsed.Count);
        Assert.All(unparsed, u =>
        {
            Assert.StartsWith("раздел ", u.GetProperty("place").GetString());
            Assert.True(u.GetProperty("line").GetInt32() > 0);
            Assert.False(string.IsNullOrWhiteSpace(u.GetProperty("reason").GetString()));
        });
        var partial = Assert.Single(report.GetProperty("marks").EnumerateArray(), m => m.GetProperty("code").GetString() == "partial");
        Assert.Contains($"Разобрано {all - 3} из {all}", partial.GetProperty("text").GetString());
    }

    /// <summary>Неизвестная версия программы — отказ с названием версии, без сводки и без редакции (ТЗ STG-12).</summary>
    [Fact]
    public async Task Неизвестная_версия_отказ_с_названием_версии()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.EditorAsync(host);
        var text = GrandSmetaSamples.DataXmlText(Lsr).Replace("ProgramVersion=\"16.2.0.58891\"", "ProgramVersion=\"17.0.0.1\"");

        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form(GrandSmetaSamples.Cp1251.GetBytes(text), "Data.xml", site.Construction, newName: "Будущая")));

        Assert.Equal("rejected", report.GetProperty("state").GetString());
        var refusal = report.GetProperty("refusal");
        Assert.Equal(nameof(GrandSmetaRefusal.UnsupportedVersion), refusal.GetProperty("code").GetString());
        Assert.Contains("17.0.0.1", refusal.GetProperty("text").GetString());
        // Вид и версию до отказа узнали — отчёт называет их и у отказа; редакции схемы нет: её не выбрали.
        Assert.Equal("lsr", report.GetProperty("kind").GetString());
        Assert.Equal("17.0.0.1", report.GetProperty("programVersion").GetString());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("schemaVersion").ValueKind);
        Assert.Equal(JsonValueKind.Null, report.GetProperty("summary").ValueKind);
        Assert.Equal(JsonValueKind.Null, report.GetProperty("revisionId").ValueKind);
    }

    /// <summary>Файл без итогов, досчитать которые нельзя, и просто не смета — тоже отказ словами разбора.</summary>
    [Fact]
    public async Task Не_файл_ГРАНД_Сметы_отказ_с_причиной()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.EditorAsync(host);

        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form("просто текст"u8.ToArray(), "смета.txt", site.Construction, newName: "Никакая")));

        Assert.Equal("rejected", report.GetProperty("state").GetString());
        Assert.Equal(nameof(GrandSmetaRefusal.NotGrandSmeta), report.GetProperty("refusal").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("kind").ValueKind);
    }

    /// <summary>ВОР разбирается, но редакцией не становится: отчёт называет вид и предлагает приложить к смете.</summary>
    [Theory]
    [InlineData("vor-15.3.7-no-attachments", "vor")]
    [InlineData("ka-16.2.0-vn-summary", "ka")]
    public async Task ВОР_и_КА_разобраны_с_видом_без_сводки(string sample, string kind)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.EditorAsync(host);

        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client, site.Construction, sample));

        Assert.Equal("parsed", report.GetProperty("state").GetString());
        Assert.Equal(kind, report.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, report.GetProperty("summary").ValueKind);
        Assert.Equal(JsonValueKind.Null, report.GetProperty("parsed").ValueKind);
        var mark = Assert.Single(report.GetProperty("marks").EnumerateArray());
        Assert.Equal("notEstimate", mark.GetProperty("code").GetString());
        Assert.Contains("Приложите файл к смете", mark.GetProperty("text").GetString());
    }

    /// <summary>Без <c>plan.estimate.cost</c> сводка — без итогов, а корень называет скрытое (ТЗ PLAN-11, D4).</summary>
    [Fact]
    public async Task Сводка_без_права_на_себестоимость_без_итога()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.EditorAsync(host);

        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client, site.Construction, Lsr));

        var summary = report.GetProperty("summary");
        Assert.True(summary.GetProperty("rows").GetInt32() > 0);
        Assert.False(summary.TryGetProperty("cost", out _));
        Assert.All(summary.GetProperty("chapters").EnumerateArray(), c => Assert.False(c.TryGetProperty("cost", out _)));
        var hidden = Assert.Single(report.GetProperty("hidden").EnumerateArray());
        Assert.Equal("cost", hidden.GetProperty("code").GetString());
        Assert.Equal("plan.estimate.cost", hidden.GetProperty("permission").GetString());
    }

    /// <summary>
    /// Импорт в смету, прежняя редакция которой — из расчёта с другим номером: отчёт показывает расхождение
    /// пометкой, решение остаётся за человеком (решение В-12 (а)).
    /// </summary>
    [Fact]
    public async Task Другой_номер_расчёта_помечен_в_отчёте()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        await PreviousImportAsync(estimate, "ЛСР-ДРУГОЙ");
        var client = await EstimateImportFlow.EditorAsync(host);

        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form(GrandSmetaSamples.Gsfx(Lsr), Lsr + ".gsfx", site.Construction, estimateId: estimate)));

        Assert.Equal(estimate, report.GetProperty("target").GetProperty("estimateId").GetGuid());
        Assert.Equal("Основная", report.GetProperty("target").GetProperty("estimateName").GetString());
        var mark = Assert.Single(report.GetProperty("marks").EnumerateArray(), m => m.GetProperty("code").GetString() == "otherCalculation");
        Assert.Contains("«ЛСР-ДРУГОЙ»", mark.GetProperty("text").GetString());
    }

    /// <summary>
    /// Прежняя редакция — последняя редакция сметы, а не последняя из импорта: заведённая поверх импорта
    /// вручную уже не тот расчёт, и сверять номер не с чем.
    /// </summary>
    [Fact]
    public async Task Номер_сверяется_с_последней_редакцией_сметы()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        await PreviousImportAsync(estimate, "ЛСР-ДРУГОЙ");
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<EstimateRevisionWriter>().AddAsync(estimate,
                new EstimateRevisionSource(), [new EstimateRowDraft("Строка вручную")]);
        var client = await EstimateImportFlow.EditorAsync(host);

        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form(GrandSmetaSamples.Gsfx(Lsr), Lsr + ".gsfx", site.Construction, estimateId: estimate)));

        Assert.DoesNotContain(report.GetProperty("marks").EnumerateArray(), m => m.GetProperty("code").GetString() == "otherCalculation");
    }

    /// <summary>Редакция сметы из прежнего импорта с этим номером расчёта — как её оставит создание редакции.</summary>
    private async Task PreviousImportAsync(Guid estimateId, string lsrNumber)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
        var estimate = await db.Estimates.AsNoTracking().SingleAsync(e => e.Id == estimateId);
        var previous = EstimateImport.IntoEstimate(estimate, "plan/прежний.gsfx", "прежний.gsfx", null, null, DateTimeOffset.UtcNow);
        previous.ParsedLocalEstimate(lsrNumber, "Прежний расчёт", "{}", "[]", 1, 1, DateTimeOffset.UtcNow);
        db.EstimateImports.Add(previous);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<EstimateRevisionWriter>().AddAsync(estimateId,
            new EstimateRevisionSource(FileName: "прежний.gsfx", ImportKey: previous.Id.ToString()), [new EstimateRowDraft("Строка")]);
    }
}
