using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SharpPdfDocument = PdfSharpCore.Pdf.PdfDocument;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Ручная корректировка разбиения PDF (GetPagesAsync/GetPageThumbnailAsync/ApplyGroupingAsync) —
/// см. архитектурный отчёт, «Ручная корректировка разбиения PDF». Признание (RecognizePdfSourceAsync
/// через живой vision-LLM) здесь не тестируется — нет фейкового IDocumentRecognizer в проекте;
/// покрыт только детерминированный, не-LLM путь (thumbnail-рендер/разрезание/confirm-gate).
/// </summary>
[Collection("Integration")]
public class DataSetPdfGroupingTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static byte[] MakePdf(int pageCount)
    {
        using var doc = new SharpPdfDocument();
        for (var i = 0; i < pageCount; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static GostGroupingGroup Doc(string code, string? name, params int[] pageIndices) =>
        new(GostGroupKind.Document, code, name,
            pageIndices.Select(i => new GostGroupingPage(i, new Dictionary<string, string?>())).ToList());

    private async Task<(Guid fileId, Guid sourceId, IServiceScope scope)> SeedGostDocumentsSourceAsync(int pageCount, GostGroupingData? grouping = null)
    {
        var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blobStorage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

        var pdfBytes = MakePdf(pageCount);
        using var uploadStream = new MemoryStream(pdfBytes);
        var blobPath = await blobStorage.UploadAsync("test.pdf", uploadStream, "application/pdf");

        var file = DataSetFile.Create("Test GOST file", DataSetFormat.Pdf, blobPath, CatalogScope.System, null);
        var source = file.AddSource("Документы", PdfProfiles.GostDocumentsMarker, "[]", 0);
        if (grouping is not null)
            file.SetGrouping(JsonSerializer.Serialize(grouping));

        db.DataSetFiles.Add(file);
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync();

        return (file.Id, source.Id, scope);
    }

    // ── GetPagesAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPagesAsync_NoGroupingYet_ReturnsEmptyDocumentsWithCorrectPageCount()
    {
        var (fileId, sourceId, scope) = await SeedGostDocumentsSourceAsync(4);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var result = await svc.GetPagesAsync(fileId, default);

            Assert.NotNull(result);
            Assert.Equal(4, result!.PageCount);
            Assert.Empty(result.Groups);
            Assert.False(result.ManuallyEdited);
        }
    }

    [Fact]
    public async Task GetPagesAsync_WithExistingGrouping_ReturnsIt()
    {
        var grouping = new GostGroupingData(
            [Doc("01-ЭМ", "План этажа", 0, 1), Doc("02-ЭМ", null, 2)],
            ManuallyEdited: false);
        var (fileId, sourceId, scope) = await SeedGostDocumentsSourceAsync(3, grouping);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var result = await svc.GetPagesAsync(fileId, default);

            Assert.NotNull(result);
            Assert.Equal(2, result!.Groups.Count);
            Assert.Equal("01-ЭМ", result.Groups[0].Code);
            Assert.Equal([0, 1], result.Groups[0].PageIndices);
        }
    }

    [Fact]
    public async Task GetPagesAsync_NonPdfFile_Throws()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = DataSetFile.Create("f", DataSetFormat.Csv, "irrelevant", CatalogScope.System, null);
        db.DataSetFiles.Add(file);
        await db.SaveChangesAsync();

        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        // Разбиение — только для PDF-набора (issue #38, редактор на уровне набора).
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetPagesAsync(file.Id, default));
    }

    // ── GetPageThumbnailAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetPageThumbnailAsync_ReturnsValidPng()
    {
        var (fileId, sourceId, scope) = await SeedGostDocumentsSourceAsync(2);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var png = await svc.GetPageThumbnailAsync(fileId, 0, default);

            Assert.NotNull(png);
            // PNG signature: 89 50 4E 47
            Assert.Equal(0x89, png![0]);
            Assert.Equal((byte)'P', png[1]);
            Assert.Equal((byte)'N', png[2]);
            Assert.Equal((byte)'G', png[3]);
        }
    }

    // ── ApplyGroupingAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ApplyGroupingAsync_OverlappingPages_Throws()
    {
        var (fileId, sourceId, scope) = await SeedGostDocumentsSourceAsync(3);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var input = new ApplyGroupingInput([
                new GostGroupingGroupDto(GostGroupKind.Document, "A", "Doc A", [0, 1]),
                new GostGroupingGroupDto(GostGroupKind.Document, "B", "Doc B", [1, 2]), // страница 1 — в обеих группах
            ]);

            await Assert.ThrowsAsync<ArgumentException>(() => svc.ApplyGroupingAsync(fileId, input, default));
        }
    }

    [Fact]
    public async Task ApplyGroupingAsync_HappyPath_SplitsAndMarksManuallyEdited()
    {
        var (fileId, sourceId, scope) = await SeedGostDocumentsSourceAsync(4);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var input = new ApplyGroupingInput([
                new GostGroupingGroupDto(GostGroupKind.Document, "01-ЭМ", "Документ 1", [0, 1]),
                new GostGroupingGroupDto(GostGroupKind.Document, "02-ЭМ", "Документ 2", [2, 3]),
            ]);

            var result = await svc.ApplyGroupingAsync(fileId, input, default);

            Assert.NotNull(result);
            Assert.True(result!.ManuallyEdited);
            Assert.Equal(2, result.Groups.Count);
            Assert.Equal(4, result.PageCount);

            // Проверяем через reload, что реестр (CachedData) обновился корректно.
            var preview = await svc.PreviewSourceAsync(sourceId, 50, default);
            Assert.NotNull(preview);
            Assert.Equal(2, preview!.TotalRows);
            var pathIdx = preview.Columns.ToList().IndexOf("ФайлПуть");
            Assert.All(preview.Rows, r => Assert.False(string.IsNullOrEmpty(r[pathIdx])));
        }
    }

    [Fact]
    public async Task ApplyGroupingAsync_PageWithNoGroup_IsExcludedFromRegistry()
    {
        var (fileId, sourceId, scope) = await SeedGostDocumentsSourceAsync(3);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            // Страница 2 не входит ни в одну группу — допустимо, просто выпадает из реестра.
            var input = new ApplyGroupingInput([new GostGroupingGroupDto(GostGroupKind.Document, "01-ЭМ", "Документ", [0, 1])]);

            var result = await svc.ApplyGroupingAsync(fileId, input, default);

            Assert.Single(result!.Groups);
            Assert.Equal([0, 1], result.Groups[0].PageIndices);
        }
    }

    [Fact]
    public async Task ApplyGroupingAsync_ReappliedGrouping_CleansUpOrphanedBlobs()
    {
        var (fileId, sourceId, scope) = await SeedGostDocumentsSourceAsync(4);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var blobStorage = (FakeBlobStorage)scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            var first = await svc.ApplyGroupingAsync(fileId,
                new ApplyGroupingInput([new GostGroupingGroupDto(GostGroupKind.Document, "01-ЭМ", "A", [0, 1, 2, 3])]), default);
            var preview1 = await svc.PreviewSourceAsync(sourceId, 50, default);
            var firstBlobPath = preview1!.Rows[0][preview1.Columns.ToList().IndexOf("ФайлПуть")];
            Assert.True(blobStorage.Exists(firstBlobPath!));

            // Перегруппировываем на 2 документа — старый общий blob должен быть удалён.
            await svc.ApplyGroupingAsync(fileId, new ApplyGroupingInput([
                new GostGroupingGroupDto(GostGroupKind.Document, "01-ЭМ", "A", [0, 1]),
                new GostGroupingGroupDto(GostGroupKind.Document, "02-ЭМ", "B", [2, 3]),
            ]), default);

            Assert.False(blobStorage.Exists(firstBlobPath!));
            _ = first;
        }
    }

    // ── RecognizePdfSourceAsync confirm-gate ─────────────────────────────────────
    // Само распознавание (вызов IDocumentRecognizer) не тестируется здесь — нет фейкового
    // распознавателя в проекте; но confirm-gate срабатывает ДО обращения к нему, так что
    // проверяем именно эту защиту без реального LLM-вызова.

    [Fact]
    public async Task RecognizePdfSourceAsync_ManuallyEditedWithoutConfirm_ThrowsInvalidOperation()
    {
        var grouping = new GostGroupingData([Doc("01-ЭМ", "Документ", 0, 1)], ManuallyEdited: true);
        var (fileId, sourceId, scope) = await SeedGostDocumentsSourceAsync(2, grouping);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RecognizePdfSourceAsync(sourceId, confirm: false, default));
        }
    }

    // ── CreatePdfSourceAsync: ГОСТ-профиль ставит профиль на набор, источников НЕ создаёт (issue #38) ──

    [Fact]
    public async Task CreatePdfSourceAsync_GostProfile_SetsProfileAndCreatesNoSource()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = DataSetFile.Create("Test PDF", DataSetFormat.Pdf, "dummy/path.pdf", CatalogScope.System, null);
        db.DataSetFiles.Add(file);
        await db.SaveChangesAsync();

        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        // ГОСТ-профиль (набор-centric): ставит PreprocessingProfile на НАБОР, источников не создаёт (null).
        var result = await svc.CreatePdfSourceAsync(file.Id, new CreatePdfSourceInput("Проект", null, PdfProfiles.GostTitleBlock), default);
        Assert.Null(result);

        var reloaded = await db.DataSetFiles.Include(f => f.Sources).FirstAsync(f => f.Id == file.Id);
        Assert.Equal(PdfProfiles.GostTitleBlock, reloaded.PreprocessingProfile);
        Assert.Empty(reloaded.Sources);
    }

    // ── ApplyGroupingAsync: инвалидация осиротевших табличных источников gost-table:* (P1b/c) ──

    // Стабильный id документа-группы (issue #28) — ключ производного табличного источника gost-table:{id}.
    private static readonly Guid SpecDocId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static GostGroupingData SpecGrouping() => new(
        [new GostGroupingGroup(GostGroupKind.Document, "01-ЭМ", "Спецификация",
            [new GostGroupingPage(0, new Dictionary<string, string?>()), new GostGroupingPage(1, new Dictionary<string, string?>())],
            ["gostDoc.specification"], SpecDocId)],
        ManuallyEdited: false);

    private static async Task<Guid> AddTableSourceAsync(AppDbContext db, Guid fileId, Guid groupId)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstAsync(f => f.Id == fileId);
        var table = file.AddSource("Таблица", PdfProfiles.GostTableMarkerPrefix + groupId, "[]", 0);
        db.DataSetSources.Add(table);
        await db.SaveChangesAsync();
        return table.Id;
    }

    [Fact]
    public async Task ApplyGroupingAsync_OrphanedTableSource_RemovedWhenDocumentUntagged()
    {
        var (fileId, sourceId, scope) = await SeedGostDocumentsSourceAsync(3, SpecGrouping());
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tableId = await AddTableSourceAsync(db, fileId, SpecDocId);

            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            // С документа снят тэг таблицы → его gost-table-источник больше не валиден и удаляется
            // (issue #28: инвалидация проекций по стабильному id группы, а не по firstPageIndex).
            await svc.ApplyGroupingAsync(fileId, new ApplyGroupingInput(
                [new GostGroupingGroupDto(GostGroupKind.Document, "01-ЭМ", "Спецификация", [0, 1], null)]), default);

            Assert.Null(await db.DataSetSources.FirstOrDefaultAsync(s => s.Id == tableId));
        }
    }

    [Fact]
    public async Task ReplaceFileAsync_PreservesGostSources_AndMarksStale()
    {
        var (fileId, sourceId, scope) = await SeedGostDocumentsSourceAsync(3);
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            // Парсер PDF возвращает [] источников — под старым поведением несвязанный gost-источник
            // был бы удалён; теперь сохраняется и помечается устаревшим.
            await svc.ReplaceFileAsync(fileId, new ReplaceFileInput(MakePdf(2), "new.pdf", "application/pdf", null), default);

            var src = await db.DataSetSources.FirstOrDefaultAsync(s => s.Id == sourceId);
            Assert.NotNull(src);
            Assert.True(src!.RecognitionStale);
        }
    }

    [Fact]
    public async Task ApplyGroupingAsync_TableSource_KeptAcrossStartShift_ViaStableGroupId()
    {
        var (fileId, sourceId, scope) = await SeedGostDocumentsSourceAsync(3, SpecGrouping());
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tableId = await AddTableSourceAsync(db, fileId, SpecDocId);

            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            // Документ сдвинул начало (стр.0→1), но остаётся тем же (пересечение страниц) и помечен
            // спецификацией → стабильный id группы переносится, таблица НЕ осиротеет (issue #28, фикс P1).
            await svc.ApplyGroupingAsync(fileId, new ApplyGroupingInput(
                [new GostGroupingGroupDto(GostGroupKind.Document, "01-ЭМ", "Спецификация", [1, 2], ["gostDoc.specification"])]), default);

            Assert.NotNull(await db.DataSetSources.FirstOrDefaultAsync(s => s.Id == tableId));
        }
    }
}
