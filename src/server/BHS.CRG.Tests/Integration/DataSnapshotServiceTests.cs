using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
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
        // Признак сдвинувшихся границ висит на ИСТОЧНИКЕ (issue #815). Прежде снимок читал TableStale
        // прямо из группировки — и видел устаревание, которого не видел клиент; группа его больше не
        // несёт для читателя, только для ре-проекции.
        if (tableStale) source.MarkRecognitionStale(DataSetStaleReason.TableBoundariesChanged);

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

    // ── Сколько строк: сырьё против выборки (issue #592) ─────────────────────────

    /// <summary>
    /// Источник с фильтром отдаёт МЕНЬШЕ строк, чем в нём лежит, и раньше об этом не говорило ни одно
    /// поле ответа: «Кабельный журнал (без ГРЩ)» показывал 48 строк в описании и 44 в выборке.
    /// Величины теперь названы по отдельности, и обе сходятся с get_rows.
    /// </summary>
    [Fact]
    public async Task SourceDetail_SeparatesRawRowsFromFiltered()
    {
        var (_, sourceId, scope) = await SeedCsvAsync(10);
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var source = await db.DataSetSources.Include(s => s.File).FirstAsync(s => s.Id == sourceId);
            // Оставляем только «Материал 1» — девять строк отсеиваются фильтром.
            source.SetProcessing(
                """{"type":"condition","column":"Наименование","operator":"equals","value":"Материал 1"}""",
                null, null);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var detail = await Svc(scope).GetSourceAsync(sourceId);

            Assert.Equal(10, detail!.RawRowCount);
            Assert.Equal(1, detail.RowCount);
            Assert.True(detail.Filtered);
            // Число из описания обязано совпадать с тем, что реально отдают строки.
            Assert.Equal(detail.RowCount, (await Svc(scope).GetRowsAsync(sourceId, 0, 50))!.TotalRows);
        }
    }

    /// <summary>Без фильтра расхождению взяться неоткуда — и признак не поднимается зря.</summary>
    [Fact]
    public async Task SourceDetail_WithoutFilter_ReportsSameCounts()
    {
        var (_, sourceId, scope) = await SeedCsvAsync(4);
        using (scope)
        {
            var detail = await Svc(scope).GetSourceAsync(sourceId);

            Assert.Equal(4, detail!.RawRowCount);
            Assert.Equal(4, detail.RowCount);
            Assert.False(detail.Filtered);
        }
    }

    /// <summary>
    /// В сводке набора точного числа нет намеренно: считать его для каждого листа значило бы скачать
    /// и разобрать файл столько раз, сколько в нём источников. Поэтому величина названа сырьём, а
    /// нужду сходить за точным числом обозначает filtered.
    /// </summary>
    [Fact]
    public async Task DatasetSummary_ReportsRawRows_AndWarnsAboutFilter()
    {
        var (fileId, sourceId, scope) = await SeedCsvAsync(10);
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var source = await db.DataSetSources.Include(s => s.File).FirstAsync(s => s.Id == sourceId);
            source.SetProcessing(
                """{"type":"condition","column":"Наименование","operator":"equals","value":"Материал 1"}""",
                null, null);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var summary = Assert.Single((await Svc(scope).GetDatasetAsync(fileId))!.Sources);

            Assert.Equal(10, summary.RawRowCount);
            Assert.True(summary.Filtered);
        }
    }

    /// <summary>
    /// Одноимённые наборы (issue #593): два набора «250701-ЭОМ-ЕЦДМ, ДНС-Сити_ИД» различались только
    /// уровнем и идентификатором. Уровень в ответе был и раньше, но выбирают по имени, а не по UUID
    /// стройки — поэтому рядом с ним теперь наименование контейнера.
    /// </summary>
    [Fact]
    public async Task DatasetList_NamesTheScope_SoSameNamedSetsDiffer()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

        var construction = await m.Send(new CreateConstructionCommand("ДНС Сити", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));

        var path = await blob.UploadAsync("a.pdf", new MemoryStream([1]), "application/pdf");
        const string sameName = "250701-ЭОМ-ЕЦДМ, ДНС-Сити_ИД";
        db.DataSetFiles.Add(DataSetFile.Create(sameName, DataSetFormat.Pdf, path, CatalogScope.Construction, construction.Id));
        db.DataSetFiles.Add(DataSetFile.Create(sameName, DataSetFormat.Pdf, path, CatalogScope.Section, section.Id));
        db.DataSetFiles.Add(DataSetFile.Create(sameName, DataSetFormat.Pdf, path, CatalogScope.System, null));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var listed = (await Svc(scope).ListDatasetsAsync(null, null)).Items
            .Where(d => d.Name == sameName)
            .ToList();

        Assert.Equal(3, listed.Count);
        Assert.Equal("ДНС Сити", Assert.Single(listed, d => d.Scope == "Construction").ScopeName);
        Assert.Equal("ЭОМ", Assert.Single(listed, d => d.Scope == "Section").ScopeName);
        // У System контейнера нет — и придумывать ему имя не надо.
        Assert.Null(Assert.Single(listed, d => d.Scope == "System").ScopeName);
    }

    // ── Повторная проверка: отпечаток строк (issue #598) ─────────────────────────

    /// <summary>
    /// Работа с комплектом итеративна («поправил реестр — перепроверь»), и каждый повтор выгружал
    /// те же строки заново: кабельный журнал за сессию выгружался не менее четырёх раз при двух
    /// реальных изменениях. Отпечаток позволяет ответить «не изменилось» вместо таблицы.
    /// </summary>
    [Fact]
    public async Task GetRows_WithMatchingHash_SkipsTheTable()
    {
        var (_, sourceId, scope) = await SeedCsvAsync(5);
        using (scope)
        {
            var svc = Svc(scope);
            var first = await svc.GetRowsAsync(sourceId, 0, 50);
            Assert.NotEmpty(first!.RowsHash);
            Assert.False(first.Unchanged);

            var again = await svc.GetRowsAsync(sourceId, 0, 50, ifNoneMatch: first.PageHash);

            Assert.True(again!.Unchanged);
            Assert.Empty(again.Rows);
            // Остальное на месте: «не изменилось» не должно выглядеть как «пусто».
            Assert.Equal(first.TotalRows, again.TotalRows);
            Assert.Equal(first.Columns, again.Columns);

            // Сквозной отпечаток отдаёт и описание источника — иначе им нельзя было бы
            // пользоваться, не выгрузив таблицу хотя бы раз.
            Assert.Equal(first.RowsHash, (await svc.GetSourceAsync(sourceId))!.RowsHash);
        }
    }

    /// <summary>
    /// Отпечаток страницы привязан к окну: иначе агент, листающий со сквозным rowsHash, получил бы
    /// на следующую страницу «не изменилось» с пустыми строками — и обход «пока truncated» встал бы
    /// навсегда, не показав ни одной строки за первой сотней.
    /// </summary>
    [Fact]
    public async Task PageHash_IsPerWindow_SoPagingStillAdvances()
    {
        var (_, sourceId, scope) = await SeedCsvAsync(10);
        using (scope)
        {
            var svc = Svc(scope);
            var first = await svc.GetRowsAsync(sourceId, 0, 4);

            // Сквозной отпечаток на роль ifNoneMatch не годится и страницу не «схлопывает».
            var next = await svc.GetRowsAsync(sourceId, 4, 4, ifNoneMatch: first!.RowsHash);
            Assert.False(next!.Unchanged);
            Assert.Equal(4, next.Rows.Count);
            Assert.Equal("5", next.Rows[0]["Позиция"]);

            // Отпечаток чужой страницы тоже не совпадёт — строки придут.
            var third = await svc.GetRowsAsync(sourceId, 8, 4, ifNoneMatch: first.PageHash);
            Assert.False(third!.Unchanged);
            Assert.Equal(2, third.Rows.Count);
        }
    }

    /// <summary>
    /// Чтение описания не должно падать из-за недоступных строк: файл набора мог быть удалён из
    /// хранилища, а метаданные, колонки и признак устаревания читаются и без него.
    /// </summary>
    [Fact]
    public async Task SourceDetail_SurvivesUnreadableRows()
    {
        var (_, sourceId, scope) = await SeedCsvAsync(3);
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var source = await db.DataSetSources.Include(s => s.File).FirstAsync(s => s.Id == sourceId);
            source.File.UpdateBlobPath("наборы/пропавший.csv", DataSetFormat.Csv);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var detail = await Svc(scope).GetSourceAsync(sourceId);

            Assert.NotNull(detail);
            Assert.Null(detail!.RowCount);
            Assert.Null(detail.RowsHash);
            Assert.False(string.IsNullOrWhiteSpace(detail.RowsError));
            // Сырое число остаётся из кэша, колонки — из схемы: описание источника всё ещё полезно.
            Assert.Equal(3, detail.RawRowCount);
            Assert.NotEmpty(detail.Columns);
        }
    }

    /// <summary>Изменились строки — отпечаток другой, и таблица приходит целиком.</summary>
    [Fact]
    public async Task GetRows_AfterChange_ReturnsRowsAgain()
    {
        var (_, sourceId, scope) = await SeedCsvAsync(5);
        using (scope)
        {
            var svc = Svc(scope);
            var before = await svc.GetRowsAsync(sourceId, 0, 50);

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var source = await db.DataSetSources.Include(s => s.File).FirstAsync(s => s.Id == sourceId);
            source.SetProcessing(
                """{"type":"condition","column":"Наименование","operator":"equals","value":"Материал 1"}""",
                null, null);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var after = await svc.GetRowsAsync(sourceId, 0, 50, ifNoneMatch: before!.RowsHash);

            Assert.False(after!.Unchanged);
            Assert.NotEqual(before.RowsHash, after.RowsHash);
            Assert.Single(after.Rows);
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
        var (_, sourceId, scope) = await SeedCsvAsync(2);
        using (scope)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False((await Svc(scope).GetSourceAsync(sourceId))!.Stale);

            var source = await db.DataSetSources.FirstAsync(x => x.Id == sourceId);
            source.MarkRecognitionStale(DataSetStaleReason.FileReplaced);
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
            var summary = Assert.Single(list.Items, d => d.Id == fileId);
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
