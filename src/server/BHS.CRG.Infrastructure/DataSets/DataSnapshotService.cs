using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <inheritdoc />
public class DataSnapshotService(
    AppDbContext db,
    IDataSetRowLoader rowLoader,
    SystemSourceCounter systemCounts) : IDataSnapshotService
{
    private static readonly JsonSerializerOptions SchemaJson = new() { PropertyNameCaseInsensitive = true };

    private record CachedColumn(string Name, string[] SampleValues);

    public async Task<SnapshotPage<DatasetSummary>> ListDatasetsAsync(
        string? scope, Guid? scopeId,
        int offset = 0, int limit = DomainSnapshotLimits.NavigationDefault,
        CancellationToken ct = default)
    {
        var q = db.DataSetFiles.AsNoTracking().Include(f => f.Sources).AsQueryable();
        if (!string.IsNullOrWhiteSpace(scope) && Enum.TryParse<Domain.Catalog.CatalogScope>(scope, true, out var s))
        {
            q = q.Where(f => f.Scope == s);
            if (scopeId is { } sid) q = q.Where(f => f.ScopeId == sid);
        }

        var files = await q.OrderBy(f => f.Name).ToListAsync(ct);
        var scopeNames = await ScopeNamesAsync(files, ct);
        var all = files.Select(f => new DatasetSummary(
            f.Id, f.Name, f.Format.ToString(), f.Scope.ToString(), f.ScopeId,
            scopeNames.GetValueOrDefault(f.ScopeId ?? Guid.Empty),
            f.Sources.Count,
            f.RecognitionStale || f.Sources.Any(s => s.RecognitionStale))).ToArray();

        return SnapshotPage<DatasetSummary>.Of(all, offset, limit, DomainSnapshotLimits.NavigationMax);
    }

    public async Task<DatasetDetail?> GetDatasetAsync(Guid datasetId, CancellationToken ct = default)
    {
        var file = await db.DataSetFiles.AsNoTracking().Include(f => f.Sources)
            .FirstOrDefaultAsync(f => f.Id == datasetId, ct);
        if (file is null) return null;

        var grouping = GostGroupingSerialization.Parse(file.Grouping);
        // Системный набор: число строк живое, кэш при создании — уже история (issue #613).
        var liveRowCounts = await systemCounts.CountAsync([file], ct);
        var sources = file.Sources
            .OrderBy(s => s.Name)
            .Select(s => new SourceSummary(
                s.Id, s.Name, OriginOf(s),
                // Сырьё, и названо сырьём (#592). Считать здесь строки ПОСЛЕ обработки значило бы
                // скачать и разобрать файл по разу на каждый лист ради навигационной выдачи; точное
                // число даёт get_source, а на его нужду указывает Filtered.
                liveRowCounts.TryGetValue(s.Id, out var live) ? live : s.CachedRowCount,
                HasFilter(s),
                IsStale(file, s, grouping, out _),
                ColumnNames(s.CachedSchema),
                SheetOf(s, grouping)))
            .ToList();

        return new DatasetDetail(
            file.Id, file.Name, file.Format.ToString(), file.Scope.ToString(), file.ScopeId,
            (await ScopeNamesAsync([file], ct)).GetValueOrDefault(file.ScopeId ?? Guid.Empty),
            file.RecognitionStale, file.PreprocessingProfile, sources);
    }

    public async Task<SourceDetail?> GetSourceAsync(Guid sourceId, CancellationToken ct = default)
    {
        var source = await db.DataSetSources.AsNoTracking().Include(s => s.File)
            .FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source?.File is null) return null;

        var grouping = GostGroupingSerialization.Parse(source.File.Grouping);
        var stale = IsStale(source.File, source, grouping, out var reason);

        // Обе величины — из ОДНОЙ загрузки: описание источника обязано сходиться с его же строками,
        // а «сколько строк» без указания, до фильтра или после, уже произвело неверный анализ (#592).
        //
        // Чтение строк добавилось здесь ради точного числа, и вместе с ним добавились его отказы:
        // удалённый блоб, недоступное хранилище, системная консолидация, которой больше нет
        // (SystemSourceCounter такие случаи глотал намеренно). Описание источника от этого не должно
        // пропадать — метаданные, колонки и признак устаревания читаются и без строк.
        LoadedRows? loaded = null;
        string? rowsError = null;
        try
        {
            loaded = await rowLoader.LoadAsync(source, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rowsError = ex.Message;
        }

        // Сырое число без строк берём из кэша — того же, что показывает сводка набора.
        var rawRowCount = loaded?.RawRowCount
            ?? await systemCounts.CountAsync(source, source.File, ct)
            ?? source.CachedRowCount;

        return new SourceDetail(
            source.Id, source.FileId, source.File.Name, source.Name,
            OriginOf(source), loaded?.Rows.Count, rawRowCount, HasFilter(source),
            stale, reason,
            source.UpdatedAt,
            loaded is null ? null : RowsFingerprint.Of(loaded.Rows), rowsError,
            Columns(source.CachedSchema),
            SheetOf(source, grouping));
    }

    public async Task<RowsPage?> GetRowsAsync(
        Guid sourceId, int offset, int limit, string? ifNoneMatch = null, CancellationToken ct = default)
    {
        var source = await db.DataSetSources.AsNoTracking().Include(s => s.File)
            .FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source?.File is null) return null;

        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit <= 0 ? IDataSnapshotService.DefaultRowsPerPage : limit,
            1, IDataSnapshotService.MaxRowsPerPage);

        // Строки ПОСЛЕ всей обработки источника (фильтр/вычисляемые колонки/сортировка) — тот же путь,
        // которым их видит генерация, поэтому внешний анализ и генерация смотрят на одни данные.
        var all = await rowLoader.LoadRowsAsync(source, ct);
        var page = all.Skip(offset).Take(limit).ToList();
        var hash = RowsFingerprint.Of(all);
        // Отпечаток СТРАНИЦЫ, а не всей выборки: «не изменилось» относится к запрошенному окну.
        // Сквозной отпечаток отвечал бы unchanged на запрос СЛЕДУЮЩЕЙ страницы с прежним хешем —
        // строки за ним агент не получил бы никогда, а обход «пока truncated» не двигался бы.
        var pageHash = RowsFingerprint.Of(page, (offset, limit));

        // Ключи колонок берём из СХЕМЫ, а не из первой строки: строка может не содержать пустых ячеек,
        // и агент, ориентируясь на неё, потерял бы колонку целиком.
        var columns = ColumnNames(source.CachedSchema);
        if (columns.Count == 0 && all.Count > 0)
            columns = [.. all.SelectMany(r => r.Keys).Distinct()];

        // Строки те же, что были у вызывающего (issue #598) — таблицу не прикладываем. Загрузку это
        // не экономит (отпечаток считается по ней же), экономит контекст: кабельный журнал за сессию
        // выгружался не менее четырёх раз при двух реальных правках.
        if (!string.IsNullOrWhiteSpace(ifNoneMatch) && string.Equals(ifNoneMatch, pageHash, StringComparison.Ordinal))
            return new RowsPage(
                source.Id, offset, limit, all.Count,
                Truncated: offset + page.Count < all.Count,
                columns, [], hash, pageHash, Unchanged: true);

        return new RowsPage(
            source.Id, offset, limit, all.Count,
            Truncated: offset + page.Count < all.Count,
            columns, page, hash, pageHash);
    }

    // ── Достоверность снимка ─────────────────────────────────────────────────────

    /// <summary>Распознанные источники помечены маркером — это и есть признак вероятностного
    /// происхождения данных (см. <see cref="PdfProfiles.IsRecognitionMarker"/>). Системная
    /// консолидация не парсится и не распознаётся — у неё своё происхождение.</summary>
    private static DataOrigin OriginOf(DataSetSource s) =>
        SystemDataSets.IsSystemMarker(s.SheetOrPath) ? DataOrigin.System
        : PdfProfiles.IsRecognitionMarker(s.SheetOrPath) ? DataOrigin.Recognized
        : DataOrigin.Parsed;

    /// <summary>Отсеивает ли источник строки. Признак нужен там, где отдаётся только сырое число:
    /// он объясняет, почему строк в выборке окажется меньше, — иначе разница выглядит потерей данных.</summary>
    private static bool HasFilter(DataSetSource s) => !string.IsNullOrWhiteSpace(s.RowFilter);

    /// <summary>
    /// Наименования контейнеров, на которых лежат наборы (issue #593): стройка, раздел, комплект.
    /// Два набора различались только уровнем и идентификатором, и в списке выбора были неразличимы —
    /// «250701-ЭОМ-ЕЦДМ, ДНС-Сити_ИД» дважды. Идентификатор человеку не говорит ничего, имя говорит.
    /// </summary>
    private async Task<Dictionary<Guid, string>> ScopeNamesAsync(
        IReadOnlyCollection<DataSetFile> files, CancellationToken ct)
    {
        var ids = files.Where(f => f.ScopeId is not null).Select(f => f.ScopeId!.Value).ToHashSet();
        if (ids.Count == 0) return [];

        var names = new Dictionary<Guid, string>();
        foreach (var (id, name) in await db.Constructions.AsNoTracking()
                     .Where(c => ids.Contains(c.Id)).Select(c => new { c.Id, c.Name })
                     .ToDictionaryAsync(c => c.Id, c => c.Name, ct))
            names[id] = name;
        foreach (var (id, name) in await db.Sections.AsNoTracking()
                     .Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.Name })
                     .ToDictionaryAsync(s => s.Id, s => s.Name, ct))
            names[id] = name;
        foreach (var (id, name) in await db.DocumentSets.AsNoTracking()
                     .Where(s => ids.Contains(s.Id)).Select(s => new { s.Id, s.Name })
                     .ToDictionaryAsync(s => s.Id, s => s.Name, ct))
            names[id] = name;
        return names;
    }

    /// <summary>Устарели ли данные источника. Три независимых причины, и каждая означает, что сверка
    /// по этим строкам может быть неверной, — поэтому возвращаем ещё и человекочитаемую причину.</summary>
    private static bool IsStale(
        DataSetFile file, DataSetSource source, GostGroupingData? grouping, out string? reason)
    {
        if (file.RecognitionStale)
        {
            reason = "Файл набора заменён после распознавания — данные относятся к прежнему содержимому.";
            return true;
        }
        if (source.RecognitionStale)
        {
            reason = "Источник помечен устаревшим — данные требуют повторного распознавания.";
            return true;
        }
        if (TableGroupOf(source, grouping) is { TableStale: true })
        {
            reason = "Состав страниц документа изменился после распознавания таблицы — строки относятся к прежним границам.";
            return true;
        }
        reason = null;
        return false;
    }

    /// <summary>Группа-документ, из которой спроецирован табличный источник (маркер несёт её id).</summary>
    private static GostGroupingGroup? TableGroupOf(DataSetSource source, GostGroupingData? grouping)
    {
        if (grouping is null) return null;
        if (!source.SheetOrPath.StartsWith(PdfProfiles.GostTableMarkerPrefix, StringComparison.Ordinal)) return null;
        var idStr = source.SheetOrPath[PdfProfiles.GostTableMarkerPrefix.Length..];
        return Guid.TryParse(idStr, out var gid) ? grouping.Groups.FirstOrDefault(g => g.Id == gid) : null;
    }

    private static SheetAnchor? SheetOf(DataSetSource source, GostGroupingData? grouping)
    {
        var group = TableGroupOf(source, grouping);
        return group is null ? null
            : new SheetAnchor(group.Code, group.Name, [.. group.Pages.Select(p => p.PageIndex).OrderBy(i => i)]);
    }

    private static IReadOnlyList<ColumnInfo> Columns(string? schemaJson)
    {
        var cols = Parse(schemaJson);
        return [.. cols.Select(c => new ColumnInfo(c.Name, c.SampleValues))];
    }

    private static IReadOnlyList<string> ColumnNames(string? schemaJson) =>
        [.. Parse(schemaJson).Select(c => c.Name)];

    private static CachedColumn[] Parse(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson)) return [];
        try { return JsonSerializer.Deserialize<CachedColumn[]>(schemaJson, SchemaJson) ?? []; }
        catch (JsonException) { return []; }
    }
}
