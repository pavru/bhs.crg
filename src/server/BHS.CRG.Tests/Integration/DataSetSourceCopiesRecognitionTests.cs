using BHS.CRG.Tests.Support;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Recognition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpPdfDocument = PdfSharpCore.Pdf.PdfDocument;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Копии источника-проекции обновляются ВМЕСТЕ (issue #1149).
///
/// «Создать копию» (issue #717) заводит второй источник на том же маркере — те же строки, другой
/// отбор. Распознавание же искало «первый источник с маркером»: свежие строки получала одна копия,
/// вторая оставалась со старыми и без пометки об устаревании. Какая из двух — решал порядок строк в
/// куче, то есть после правки любой из них это была уже другая копия.
///
/// Проверяются все четыре места, где проекция обновляется по маркеру: счёт, обложка/титул/документы
/// альбома, таблица документа и пометка устаревания при смене профиля.
/// </summary>
[Collection("Integration")]
public class DataSetSourceCopiesRecognitionTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly Guid DocId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static byte[] MakePdf(int pageCount)
    {
        using var doc = new SharpPdfDocument();
        for (var i = 0; i < pageCount; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>Распознаватель, который ничего не нашёл: содержимое строк здесь не проверяется —
    /// проверяется, КОМУ оно досталось.</summary>
    private sealed class EmptyRecognizer : IDocumentRecognizer
    {
        public Task<RecognitionResult> RecognizeAsync(
            byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
            Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default) =>
            Task.FromResult(new RecognitionResult(new Dictionary<string, string?>(), null));
    }

    private static DataSetPdfRecognitionService Recognition(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return new DataSetPdfRecognitionService(
            db, scope.ServiceProvider.GetRequiredService<IBlobStorage>(), new EmptyRecognizer(),
            scope.ServiceProvider.GetRequiredService<INotificationService>(), new RecognitionProfileProvider(db, TestRecognition.Catalog),
            NullLogger<DataSetPdfRecognitionService>.Instance);
    }

    /// <summary>PDF-набор с источниками на заданных маркерах — по ДВА на каждый: оригинал и копия.</summary>
    private static async Task<Guid> SeedWithCopiesAsync(
        IServiceScope scope, string? profile, GostGroupingData? grouping, params string[] markers)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await RecognitionProfileSeeder.SeedAsync(db, TestRecognition.Catalog);
        db.ChangeTracker.Clear();

        using var upload = new MemoryStream(MakePdf(3));
        var blobPath = await scope.ServiceProvider.GetRequiredService<IBlobStorage>()
            .UploadAsync("test.pdf", upload, "application/pdf");

        var file = DataSetFile.Create("Набор", DataSetFormat.Pdf, blobPath, CatalogScope.System, null);
        file.SetPreprocessingProfile(profile);
        if (grouping is not null) file.SetGrouping(JsonSerializer.Serialize(grouping));
        db.DataSetFiles.Add(file);
        foreach (var marker in markers)
        {
            db.DataSetSources.Add(file.AddSource("Источник", marker, "[]", 0));
            db.DataSetSources.Add(file.AddSource("Источник — 2", marker, "[]", 0));
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return file.Id;
    }

    private static Task<List<DataSetSource>> StoredAsync(IServiceScope scope, Guid fileId) =>
        scope.ServiceProvider.GetRequiredService<AppDbContext>().DataSetSources.AsNoTracking()
            .Where(s => s.FileId == fileId).ToListAsync();

    private static GostGroupingData TableGrouping(Guid? profileId, bool recognized) => new(
        [new GostGroupingGroup(GostGroupKind.Document, "A113", "Список деталей ТКШ1",
            [new GostGroupingPage(0, new Dictionary<string, string?>()), new GostGroupingPage(1, new Dictionary<string, string?>())],
            Tags: null, Id: DocId, ProfileId: profileId,
            TableData: recognized ? """[{"Поз":"1"}]""" : null,
            TableColumns: recognized ? """[{"name":"Поз"}]""" : null)],
        ManuallyEdited: false);

    private static async Task<Guid> TableProfileAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profile = RecognitionProfile.Create(
            "Список деталей шкафа", RecognitionProfileKind.Table, TestRecognition.OwnerOf(RecognitionProfileKind.Table),
            fields: RecognitionProfileJson.WriteFields([]),
            rowColumns: RecognitionProfileJson.WriteFields([new RecognitionProfileField("Поз", "Позиция")]),
            shape: RecognitionProfileJson.WriteShape(new RecognitionTableShape(TwoTierHeader: false)));
        db.RecognitionProfiles.Add(profile);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return profile.Id;
    }

    [Fact]
    public async Task InvoiceRecognition_RefreshesEveryCopy_OfHeaderAndLineItems()
    {
        using var scope = fixture.Services.CreateScope();
        var fileId = await SeedWithCopiesAsync(scope, PdfProfiles.Invoice, grouping: null,
            PdfProfiles.InvoiceHeaderMarker, PdfProfiles.InvoiceLineItemsMarker);

        await Recognition(scope).RecognizeFileAsync(fileId, confirm: true, default);

        // Строки распознавания есть у всех четырёх: до него кэша не было ни у одного.
        var sources = await StoredAsync(scope, fileId);
        Assert.Equal(4, sources.Count);
        Assert.All(sources, s => Assert.NotNull(s.CachedData));
    }

    [Fact]
    public async Task Regrouping_RefreshesEveryCopy_OfDocuments()
    {
        using var scope = fixture.Services.CreateScope();
        var fileId = await SeedWithCopiesAsync(scope, PdfProfiles.GostTitleBlock, grouping: null,
            PdfProfiles.GostDocumentsMarker);

        await scope.ServiceProvider.GetRequiredService<IDataSetService>().ApplyGroupingAsync(fileId,
            new ApplyGroupingInput([new GostGroupingGroupDto(GostGroupKind.Document, "01-ЭМ", "Документ", [0, 1])]), default);

        var sources = await StoredAsync(scope, fileId);
        Assert.Equal(2, sources.Count);
        Assert.All(sources, s => Assert.Equal(1, s.CachedRowCount));
    }

    [Fact]
    public async Task TableRecognition_RefreshesEveryCopy_OfTheTable()
    {
        using var scope = fixture.Services.CreateScope();
        var profileId = await TableProfileAsync(scope);
        var fileId = await SeedWithCopiesAsync(scope, PdfProfiles.GostTitleBlock, TableGrouping(profileId, recognized: false),
            PdfProfiles.GostTableMarkerPrefix + DocId);

        await Recognition(scope).RecognizeDocumentTableAsync(fileId, firstPageIndex: 0, default);

        var sources = await StoredAsync(scope, fileId);
        Assert.Equal(2, sources.Count);
        Assert.All(sources, s => Assert.NotNull(s.CachedData));
    }

    [Fact]
    public async Task ProfileChange_MarksEveryCopy_OfTheTableStale()
    {
        using var scope = fixture.Services.CreateScope();
        var profileId = await TableProfileAsync(scope);
        var fileId = await SeedWithCopiesAsync(scope, PdfProfiles.GostTitleBlock, TableGrouping(null, recognized: true),
            PdfProfiles.GostTableMarkerPrefix + DocId);

        await scope.ServiceProvider.GetRequiredService<IDataSetService>()
            .SetDocumentProfileAsync(fileId, firstPageIndex: 0, profileId, default);

        var sources = await StoredAsync(scope, fileId);
        Assert.Equal(2, sources.Count);
        Assert.All(sources, s => Assert.Equal(DataSetStaleReason.ProfileChanged, s.StaleReason));
    }
}
