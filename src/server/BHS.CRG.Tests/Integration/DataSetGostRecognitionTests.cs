using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Recognition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpPdfDocument = PdfSharpCore.Pdf.PdfDocument;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сквозной интеграционный тест распознавания ГОСТ-комплекта (RecognizeGostSetAsync) — ранее не
/// покрыт (был только детерминированный не-LLM путь, см. DataSetPdfGroupingTests). Vision-LLM
/// заменён сценарным фейком <see cref="ScriptedRecognizer"/>: он различает первый проход (полная
/// страница — набор полей содержит классификаторы ТипСтраницы/Форма) и второй проход (кроп штампа —
/// только базовые поля) и отдаёт заранее заданные значения по порядку страниц. Проверяется весь
/// конвейер: маршрутизация обложка/титул/документы, группировка по форме, физическое разрезание,
/// запись кэшей трёх источников и итоговой группировки.
/// </summary>
[Collection("Integration")]
public class DataSetGostRecognitionTests(IntegrationTestFixture fixture) : IAsyncLifetime
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

    /// <summary>Фейк распознавателя: по порядку страниц отдаёт сценарные значения на первом проходе;
    /// на втором проходе (кроп) возвращает пусто, чтобы значения решал первый проход.</summary>
    private sealed class ScriptedRecognizer(IReadOnlyList<IReadOnlyDictionary<string, string?>> perPage) : IDocumentRecognizer
    {
        private int _page;

        public Task<RecognitionResult> RecognizeAsync(
            byte[] file, string mimeType, IReadOnlyList<RecognitionField> fields,
            Func<IReadOnlyList<RecognitionField>, string>? promptBuilder = null, CancellationToken ct = default)
        {
            // Проход заглавного листа (обложка/титул) — свой набор полей (НаименованиеКомплекта);
            // отдаём фиксированные реквизиты, не трогая счётчик страниц пасс-1.
            if (fields.Any(f => f.Path == "НаименованиеКомплекта"))
                return Task.FromResult(new RecognitionResult(new Dictionary<string, string?>
                {
                    ["ОбъектСтроительства"] = "ЖК Тест",
                    ["НаименованиеКомплекта"] = "Электроосвещение",
                    ["Организация"] = "ООО Проект",
                }, null));

            var isFullPagePass = fields.Any(f => f.Path == GostTitleBlockFields.PageTypePath);
            if (!isFullPagePass)
                return Task.FromResult(new RecognitionResult(new Dictionary<string, string?>(), null));

            var values = perPage[_page++];
            return Task.FromResult(new RecognitionResult(values, null));
        }
    }

    private static Dictionary<string, string?> P(string pageType, string? form = null, string? shifr = null, string? name = null)
    {
        var d = new Dictionary<string, string?> { ["ТипСтраницы"] = pageType };
        if (form is not null) d["Форма"] = form;
        if (shifr is not null) d["Шифр"] = shifr;
        if (name is not null) d["НаименованиеДокумента"] = name;
        return d;
    }

    [Fact]
    public async Task RecognizeGostSet_RoutesCoverTitleAndGroupsDocumentsByForm()
    {
        // 7 страниц: обложка, титул, затем документы —
        // Форма3 «Общие данные», Форма3 «Схема 1» + её Форма6-продолжение, Форма5 «Спецификация» + её Форма6.
        var script = new IReadOnlyDictionary<string, string?>[]
        {
            P("Обложка"),
            P("ТитульныйЛист"),
            P("Документ", "Форма3", "01-ЭМ", "Общие данные"),
            P("Документ", "Форма3", "01-ЭМ", "Схема 1"),
            P("Документ", "Форма6", "01-ЭМ", "лишнее имя из таблицы"), // должно быть отброшено (форма 6)
            P("Документ", "Форма5", "01-ЭМ.СО", "Спецификация"),
            P("Документ", "Форма6", "01-ЭМ.СО"),
        };

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

        var pdfBytes = MakePdf(7);
        using var uploadStream = new MemoryStream(pdfBytes);
        var blobPath = await blob.UploadAsync("gost.pdf", uploadStream, "application/pdf");

        var file = DataSetFile.Create("GOST комплект", DataSetFormat.Pdf, blobPath, CatalogScope.System, null);
        var cover = file.AddSource("Обложка", PdfProfiles.GostCoverMarker, "[]", 0);
        var titlePage = file.AddSource("Титульный лист", PdfProfiles.GostTitlePageMarker, "[]", 0);
        var documents = file.AddSource("Документы", PdfProfiles.GostDocumentsMarker, "[]", 0);
        db.DataSetFiles.Add(file);
        db.DataSetSources.AddRange(cover, titlePage, documents);
        await db.SaveChangesAsync();

        var notifications = new RecordingNotificationService();
        var svc = new DataSetPdfRecognitionService(
            db, blob, new ScriptedRecognizer(script), notifications, NullLogger<DataSetPdfRecognitionService>.Instance);
        await svc.RecognizePdfSourceAsync(documents.Id, confirm: true, default);

        // Обложка и титул — по одной строке каждая.
        Assert.Equal(1, cover.CachedRowCount);
        Assert.Equal(1, titlePage.CachedRowCount);

        // Обложка/титул распознаны СВОИМ набором полей (реквизиты заглавного листа), а не пустым штампом.
        var coverRows = JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(cover.CachedData!)!;
        Assert.Equal("ЖК Тест", coverRows[0].GetValueOrDefault("ОбъектСтроительства"));
        Assert.Equal("Электроосвещение", coverRows[0].GetValueOrDefault("НаименованиеКомплекта"));

        // Итоговое уведомление опубликовано — Info (без частичных сбоев).
        Assert.Contains(notifications.Published,
            n => n.Severity == NotificationSeverity.Info && n.Title == "Распознавание групп листов PDF завершено");

        // Обложка и титул — как группы в единой группировке.
        var grouping = JsonSerializer.Deserialize<GostGroupingData>(db.DataSetFiles.Find(documents.FileId)!.Grouping!)!;
        Assert.False(grouping.ManuallyEdited);
        Assert.Contains(grouping.Groups, g => g.Kind == GostGroupKind.Cover);
        Assert.Contains(grouping.Groups, g => g.Kind == GostGroupKind.TitlePage);

        // Документы — 3 группы: [2], [3,4], [5,6] (0-based).
        Assert.Equal(3, documents.CachedRowCount);
        var docs = Docs(grouping);
        Assert.Equal(3, docs.Count);

        Assert.Equal([2], Idx(docs[0]));
        Assert.Equal("Общие данные", docs[0].Name);

        Assert.Equal([3, 4], Idx(docs[1])); // Форма3 + Форма6-продолжение
        Assert.Equal("Схема 1", docs[1].Name);

        Assert.Equal([5, 6], Idx(docs[2])); // Форма5 + Форма6-продолжение
        Assert.Equal("Спецификация", docs[2].Name);

        // Каждая группа документов физически разрезана — у каждой строки реестра есть ФайлПуть/blob.
        var rows = JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(documents.CachedData!)!;
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.True(r.TryGetValue("ФайлПуть", out var path) && !string.IsNullOrEmpty(path));
            Assert.True(((FakeBlobStorage)blob).Exists(path!));
        });
    }

    [Fact]
    public async Task RecognizeGostSet_Form6ContinuationDoesNotSplitDespiteDifferentShifr()
    {
        // Первый лист Форма3, затем Форма6 с ЗАШУМЛЁННЫМ (другим) шифром — не должно порождать
        // новую группу: форма 6 всегда продолжает текущий документ.
        var script = new IReadOnlyDictionary<string, string?>[]
        {
            P("Документ", "Форма3", "DP-ЕЦДМ-ЭМ", "Схема"),
            P("Документ", "Форма6", "ДР-ЕЦ.ДМ-ЭМ"), // шум в шифре
        };

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

        var pdfBytes = MakePdf(2);
        using var uploadStream = new MemoryStream(pdfBytes);
        var blobPath = await blob.UploadAsync("gost.pdf", uploadStream, "application/pdf");

        var file = DataSetFile.Create("GOST", DataSetFormat.Pdf, blobPath, CatalogScope.System, null);
        var cover = file.AddSource("Обложка", PdfProfiles.GostCoverMarker, "[]", 0);
        var titlePage = file.AddSource("Титул", PdfProfiles.GostTitlePageMarker, "[]", 0);
        var documents = file.AddSource("Документы", PdfProfiles.GostDocumentsMarker, "[]", 0);
        db.DataSetFiles.Add(file);
        db.DataSetSources.AddRange(cover, titlePage, documents);
        await db.SaveChangesAsync();

        var svc = new DataSetPdfRecognitionService(
            db, blob, new ScriptedRecognizer(script), new RecordingNotificationService(), NullLogger<DataSetPdfRecognitionService>.Instance);
        await svc.RecognizePdfSourceAsync(documents.Id, confirm: true, default);

        var grouping = JsonSerializer.Deserialize<GostGroupingData>(db.DataSetFiles.Find(documents.FileId)!.Grouping!)!;
        var group = Assert.Single(Docs(grouping));
        Assert.Equal([0, 1], Idx(group));
        Assert.Equal("DP-ЕЦДМ-ЭМ", group.Code); // код с первого листа, не с зашумлённого продолжения
    }

    [Fact]
    public async Task RecognizeDocument_RefreshesOnlyTargetDocument_LeavesOthers()
    {
        var script = new IReadOnlyDictionary<string, string?>[]
        {
            P("Обложка"),
            P("ТитульныйЛист"),
            P("Документ", "Форма3", "01-ЭМ", "Схема 1"),
            P("Документ", "Форма5", "02-ЭМ", "Спецификация"),
        };
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

        var pdfBytes = MakePdf(4);
        using var uploadStream = new MemoryStream(pdfBytes);
        var blobPath = await blob.UploadAsync("gost.pdf", uploadStream, "application/pdf");
        var file = DataSetFile.Create("GOST", DataSetFormat.Pdf, blobPath, CatalogScope.System, null);
        var cover = file.AddSource("Обложка", PdfProfiles.GostCoverMarker, "[]", 0);
        var titlePage = file.AddSource("Титул", PdfProfiles.GostTitlePageMarker, "[]", 0);
        var documents = file.AddSource("Документы", PdfProfiles.GostDocumentsMarker, "[]", 0);
        db.DataSetFiles.Add(file);
        db.DataSetSources.AddRange(cover, titlePage, documents);
        await db.SaveChangesAsync();

        var svc = new DataSetPdfRecognitionService(db, blob, new ScriptedRecognizer(script),
            new RecordingNotificationService(), NullLogger<DataSetPdfRecognitionService>.Instance);
        await svc.RecognizePdfSourceAsync(documents.Id, confirm: true, default);

        // Перераспознаём ТОЛЬКО документ на стр.2 (новое имя); документ на стр.3 не трогаем.
        var svc2 = new DataSetPdfRecognitionService(db, blob,
            new ScriptedRecognizer([P("Документ", "Форма3", "01-ЭМ", "Схема 1 (испр.)")]),
            new RecordingNotificationService(), NullLogger<DataSetPdfRecognitionService>.Instance);
        await svc2.RecognizeDocumentAsync(documents.Id, firstPageIndex: 2, default);

        var grouping = JsonSerializer.Deserialize<GostGroupingData>(db.DataSetFiles.Find(documents.FileId)!.Grouping!)!;
        var docs = Docs(grouping);
        Assert.Equal("Схема 1 (испр.)", docs.Single(d => d.Pages[0].PageIndex == 2).Name); // целевой обновлён
        Assert.Equal("Спецификация", docs.Single(d => d.Pages[0].PageIndex == 3).Name);    // соседний не тронут
    }

    private static List<GostGroupingGroup> Docs(GostGroupingData g) =>
        g.Groups.Where(x => x.Kind == GostGroupKind.Document).ToList();

    private static int[] Idx(GostGroupingGroup g) => g.Pages.Select(p => p.PageIndex).ToArray();

    /// <summary>Фейк уведомлений — записывает публикации (проверяем итог распознавания), остальное не нужно.</summary>
    private sealed class RecordingNotificationService : INotificationService
    {
        public readonly List<(NotificationSeverity Severity, string Title, string Message)> Published = [];
        public Task PublishAsync(NotificationSeverity severity, string title, string message,
            string? source = null, Guid? userId = null, string? linkUrl = null, string? linkLabel = null, CancellationToken ct = default)
        {
            Published.Add((severity, title, message));
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly = false, int take = 100, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task MarkReadAsync(Guid id, Guid userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task MarkAllReadAsync(Guid userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DismissAsync(Guid id, Guid userId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearAsync(Guid userId, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
