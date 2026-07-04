using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
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

    private async Task<(Guid sourceId, IServiceScope scope)> SeedGostDocumentsSourceAsync(int pageCount, GostGroupingData? grouping = null)
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
            source.SetGostGrouping(JsonSerializer.Serialize(grouping));

        db.DataSetFiles.Add(file);
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync();

        return (source.Id, scope);
    }

    // ── GetPagesAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPagesAsync_NoGroupingYet_ReturnsEmptyDocumentsWithCorrectPageCount()
    {
        var (sourceId, scope) = await SeedGostDocumentsSourceAsync(4);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var result = await svc.GetPagesAsync(sourceId, default);

            Assert.NotNull(result);
            Assert.Equal(4, result!.PageCount);
            Assert.Empty(result.Documents);
            Assert.False(result.ManuallyEdited);
        }
    }

    [Fact]
    public async Task GetPagesAsync_WithExistingGrouping_ReturnsIt()
    {
        var grouping = new GostGroupingData(
            [new GostGroupingDocument("01-ЭМ", "План этажа", [0, 1]), new GostGroupingDocument("02-ЭМ", null, [2])],
            ManuallyEdited: false);
        var (sourceId, scope) = await SeedGostDocumentsSourceAsync(3, grouping);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var result = await svc.GetPagesAsync(sourceId, default);

            Assert.NotNull(result);
            Assert.Equal(2, result!.Documents.Count);
            Assert.Equal("01-ЭМ", result.Documents[0].Code);
            Assert.Equal([0, 1], result.Documents[0].PageIndices);
        }
    }

    [Fact]
    public async Task GetPagesAsync_WrongSourceType_Throws()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = DataSetFile.Create("f", DataSetFormat.Pdf, "irrelevant", CatalogScope.System, null);
        var source = file.AddSource("Обложка", PdfProfiles.GostCoverMarker, "[]", 0);
        db.DataSetFiles.Add(file);
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync();

        var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.GetPagesAsync(source.Id, default));
    }

    // ── GetPageThumbnailAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetPageThumbnailAsync_ReturnsValidPng()
    {
        var (sourceId, scope) = await SeedGostDocumentsSourceAsync(2);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var png = await svc.GetPageThumbnailAsync(sourceId, 0, default);

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
        var (sourceId, scope) = await SeedGostDocumentsSourceAsync(3);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var input = new ApplyGroupingInput([
                new GostGroupingDocumentDto("A", "Doc A", [0, 1]),
                new GostGroupingDocumentDto("B", "Doc B", [1, 2]), // страница 1 — в обеих группах
            ]);

            await Assert.ThrowsAsync<ArgumentException>(() => svc.ApplyGroupingAsync(sourceId, input, default));
        }
    }

    [Fact]
    public async Task ApplyGroupingAsync_HappyPath_SplitsAndMarksManuallyEdited()
    {
        var (sourceId, scope) = await SeedGostDocumentsSourceAsync(4);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var input = new ApplyGroupingInput([
                new GostGroupingDocumentDto("01-ЭМ", "Документ 1", [0, 1]),
                new GostGroupingDocumentDto("02-ЭМ", "Документ 2", [2, 3]),
            ]);

            var result = await svc.ApplyGroupingAsync(sourceId, input, default);

            Assert.NotNull(result);
            Assert.True(result!.ManuallyEdited);
            Assert.Equal(2, result.Documents.Count);
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
        var (sourceId, scope) = await SeedGostDocumentsSourceAsync(3);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            // Страница 2 не входит ни в одну группу — допустимо, просто выпадает из реестра.
            var input = new ApplyGroupingInput([new GostGroupingDocumentDto("01-ЭМ", "Документ", [0, 1])]);

            var result = await svc.ApplyGroupingAsync(sourceId, input, default);

            Assert.Single(result!.Documents);
            Assert.Equal([0, 1], result.Documents[0].PageIndices);
        }
    }

    [Fact]
    public async Task ApplyGroupingAsync_ReappliedGrouping_CleansUpOrphanedBlobs()
    {
        var (sourceId, scope) = await SeedGostDocumentsSourceAsync(4);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            var blobStorage = (FakeBlobStorage)scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            var first = await svc.ApplyGroupingAsync(sourceId,
                new ApplyGroupingInput([new GostGroupingDocumentDto("01-ЭМ", "A", [0, 1, 2, 3])]), default);
            var preview1 = await svc.PreviewSourceAsync(sourceId, 50, default);
            var firstBlobPath = preview1!.Rows[0][preview1.Columns.ToList().IndexOf("ФайлПуть")];
            Assert.True(blobStorage.Exists(firstBlobPath!));

            // Перегруппировываем на 2 документа — старый общий blob должен быть удалён.
            await svc.ApplyGroupingAsync(sourceId, new ApplyGroupingInput([
                new GostGroupingDocumentDto("01-ЭМ", "A", [0, 1]),
                new GostGroupingDocumentDto("02-ЭМ", "B", [2, 3]),
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
        var grouping = new GostGroupingData([new GostGroupingDocument("01-ЭМ", "Документ", [0, 1])], ManuallyEdited: true);
        var (sourceId, scope) = await SeedGostDocumentsSourceAsync(2, grouping);
        using (scope)
        {
            var svc = scope.ServiceProvider.GetRequiredService<IDataSetService>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RecognizePdfSourceAsync(sourceId, confirm: false, default));
        }
    }
}
