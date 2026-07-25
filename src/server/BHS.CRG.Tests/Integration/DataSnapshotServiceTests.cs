using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Снимок данных для внешнего агента (issue #415). Здесь проверяется не «отдаются ли данные», а то,
/// без чего внешняя сверка станет НЕВЕРНОЙ: заметность усечения, признак устаревания, происхождение
/// строк и якорь на исходные листы.
/// </summary>
[Collection("Integration")]
public class DataSnapshotServiceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly Guid GroupId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static IDataSnapshotService Svc(IServiceScope s) =>
        s.ServiceProvider.GetRequiredService<IDataSnapshotService>();

    /// <summary>CSV-набор с N строками — детерминированный источник, без распознавания.</summary>
    private async Task<(Guid fileId, Guid sourceId, IServiceScope scope)> SeedCsvAsync(int rowCount)
    {
        var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

        // CsvDataSetParser определяет разделитель как таб/запятую — точка с запятой дала бы одну
        // склеенную колонку, и тест проверял бы не то, что нужно.
        var csv = "Позиция,Наименование\n" +
                  string.Join('\n', Enumerable.Range(1, rowCount).Select(i => $"{i},Материал {i}"));
        var blobPath = await blob.UploadAsync("m.csv", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv)), "text/csv");

        var file = DataSetFile.Create("Реестр материалов", DataSetFormat.Csv, blobPath, CatalogScope.System, null);
        var schema = JsonSerializer.Serialize(new[]
        {
            new { name = "Позиция", sampleValues = new[] { "1" } },
            new { name = "Наименование", sampleValues = new[] { "Материал 1" } },
        });
        var source = file.AddSource("Материалы", "default", schema, rowCount);
        db.DataSetFiles.Add(file);
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync();
        return (file.Id, source.Id, scope);
    }

    /// <summary>PDF-набор с табличным источником-проекцией — распознанные данные с якорем на листы.</summary>
    private async Task<(Guid fileId, Guid sourceId, IServiceScope scope)> SeedRecognizedTableAsync(bool tableStale)
    {
        var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var blobPath = await blob.UploadAsync("a.pdf", new MemoryStream([1, 2, 3]), "application/pdf");

        var rows = new[] { new Dictionary<string, string?> { ["Поз"] = "1", ["Наименование"] = "Патч-панель" } };
        var grouping = new GostGroupingData(
            [new GostGroupingGroup(GostGroupKind.Document, "A113", "Список деталей ТКШ1",
                [new GostGroupingPage(12, new Dictionary<string, string?>()),
                 new GostGroupingPage(13, new Dictionary<string, string?>())],
                Tags: null, Id: GroupId, TableStale: tableStale)],
            ManuallyEdited: false);

        var file = DataSetFile.Create("Альбом СС", DataSetFormat.Pdf, blobPath, CatalogScope.System, null);
        file.SetGrouping(JsonSerializer.Serialize(grouping));
        var schema = JsonSerializer.Serialize(new[]
        {
            new { name = "Поз", sampleValues = new[] { "1" } },
            new { name = "Наименование", sampleValues = new[] { "Патч-панель" } },
        });
        var source = file.AddSource("Таблица — Список деталей ТКШ1",
            PdfProfiles.GostTableMarkerPrefix + GroupId, schema, rows.Length, null, JsonSerializer.Serialize(rows));

        db.DataSetFiles.Add(file);
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync();
        return (file.Id, source.Id, scope);
    }

    // ── Полнота выборки: тихое усечение — худший вид отказа ───────────────────────

    [Fact]
    public async Task GetRows_MarksTruncation_AndReportsTotal()
    {
        var (_, sourceId, scope) = await SeedCsvAsync(250);
        using (scope)
        {
            var svc = Svc(scope);

            var first = await svc.GetRowsAsync(sourceId, offset: 0, limit: 100);
            Assert.NotNull(first);
            Assert.Equal(250, first!.TotalRows);
            Assert.Equal(100, first.Rows.Count);
            Assert.True(first.Truncated);   // агент обязан увидеть, что это НЕ вся таблица

            var last = await svc.GetRowsAsync(sourceId, offset: 200, limit: 100);
            Assert.Equal(50, last!.Rows.Count);
            Assert.False(last.Truncated);   // последняя страница — усечения нет

            // Смещение адресует строку: порядок стабилен, ordinal = offset + позиция в массиве.
            Assert.Equal("201", last.Rows[0]["Позиция"]);
        }
    }

    [Fact]
    public async Task GetRows_ClampsLimit_ToHardCeiling()
    {
        var (_, sourceId, scope) = await SeedCsvAsync(600);
        using (scope)
        {
            // Запрос «отдай всё» не должен молча выдать всё: потолок защищает и контекст агента,
            // и от иллюзии полной выборки.
            var page = await svc_GetRows(scope, sourceId, 0, 10_000);
            Assert.Equal(IDataSnapshotService.MaxRowsPerPage, page.Limit);
            Assert.Equal(IDataSnapshotService.MaxRowsPerPage, page.Rows.Count);
            Assert.True(page.Truncated);
            Assert.Equal(600, page.TotalRows);

            // Некорректный лимит → значение по умолчанию, а не исключение и не «всё».
            var def = await svc_GetRows(scope, sourceId, 0, 0);
            Assert.Equal(IDataSnapshotService.DefaultRowsPerPage, def.Rows.Count);
        }

        static async Task<RowsPage> svc_GetRows(IServiceScope s, Guid id, int offset, int limit)
            => (await Svc(s).GetRowsAsync(id, offset, limit))!;
    }

    [Fact]
    public async Task GetRows_ColumnsComeFromSchema_NotFromFirstRow()
    {
        // Строка может не содержать пустых ячеек; ориентируясь на первую строку, агент потерял бы
        // колонку целиком и счёл бы её отсутствующей в документе.
        var (_, sourceId, scope) = await SeedCsvAsync(3);
        using (scope)
        {
            var page = await Svc(scope).GetRowsAsync(sourceId, 0, 10);
            Assert.Equal(["Позиция", "Наименование"], page!.Columns);
        }
    }

    // ── Достоверность: происхождение и свежесть ──────────────────────────────────

    [Fact]
    public async Task Origin_DistinguishesRecognizedFromParsed()
    {
        var (_, csvSourceId, csvScope) = await SeedCsvAsync(2);
        using (csvScope)
            Assert.Equal(DataOrigin.Parsed, (await Svc(csvScope).GetSourceAsync(csvSourceId))!.Origin);

        var (_, pdfSourceId, pdfScope) = await SeedRecognizedTableAsync(tableStale: false);
        using (pdfScope)
        {
            // Правило проекта «истина в xml, pdf — производное» держится именно на этом различии.
            var detail = await Svc(pdfScope).GetSourceAsync(pdfSourceId);
            Assert.Equal(DataOrigin.Recognized, detail!.Origin);
        }
    }

    [Fact]
    public async Task Stale_ReflectsReplacedFile_WithReason()
    {
        var (fileId, sourceId, scope) = await SeedCsvAsync(2);
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False((await Svc(scope).GetSourceAsync(sourceId))!.Stale);

            var file = await db.DataSetFiles.FirstAsync(f => f.Id == fileId);
            file.MarkRecognitionStale();
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var detail = await Svc(scope).GetSourceAsync(sourceId);
            Assert.True(detail!.Stale);
            Assert.False(string.IsNullOrWhiteSpace(detail.StaleReason)); // причина, а не голый флаг
        }
    }

    [Fact]
    public async Task Stale_ReflectsChangedDocumentBoundaries()
    {
        var (_, sourceId, scope) = await SeedRecognizedTableAsync(tableStale: true);
        using (scope)
        {
            // Состав страниц документа изменился после распознавания таблицы — строки относятся к
            // прежним границам, сверять по ним нельзя.
            var detail = await Svc(scope).GetSourceAsync(sourceId);
            Assert.True(detail!.Stale);
            Assert.Contains("границ", detail.StaleReason!, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Якорь на исходные листы ──────────────────────────────────────────────────

    [Fact]
    public async Task SheetAnchor_PointsToSourcePages_ForRecognizedTable()
    {
        var (_, sourceId, scope) = await SeedRecognizedTableAsync(tableStale: false);
        using (scope)
        {
            var detail = await Svc(scope).GetSourceAsync(sourceId);
            Assert.NotNull(detail!.Sheet);
            Assert.Equal("A113", detail.Sheet!.Code);
            Assert.Equal("Список деталей ТКШ1", detail.Sheet.Name);
            Assert.Equal([12, 13], detail.Sheet.Pages);   // колонка отчёта «Файлы / листы»
        }
    }

    [Fact]
    public async Task SheetAnchor_IsNull_ForNonPdfSource()
    {
        var (_, sourceId, scope) = await SeedCsvAsync(2);
        using (scope)
            Assert.Null((await Svc(scope).GetSourceAsync(sourceId))!.Sheet);
    }

    // ── Навигация ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAndGetDataset_GiveEntryPointAndStructure()
    {
        var (fileId, sourceId, scope) = await SeedRecognizedTableAsync(tableStale: false);
        using (scope)
        {
            var svc = Svc(scope);

            var list = await svc.ListDatasetsAsync(null, null);
            var summary = Assert.Single(list, d => d.Id == fileId);
            Assert.Equal(1, summary.SourceCount);

            var detail = await svc.GetDatasetAsync(fileId);
            var src = Assert.Single(detail!.Sources);
            Assert.Equal(sourceId, src.Id);
            Assert.Equal(DataOrigin.Recognized, src.Origin);
            Assert.Equal(["Поз", "Наименование"], src.Columns);
        }
    }

    [Fact]
    public async Task Missing_ReturnsNull_NotThrows()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        Assert.Null(await svc.GetDatasetAsync(Guid.NewGuid()));
        Assert.Null(await svc.GetSourceAsync(Guid.NewGuid()));
        Assert.Null(await svc.GetRowsAsync(Guid.NewGuid(), 0, 10));
    }
}
