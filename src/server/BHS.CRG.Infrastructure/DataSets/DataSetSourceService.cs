using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Recognition;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Источники набора данных (обычные, не PDF-распознавание): CRUD/preview/export/автомаппинг/duplicate,
/// zip-entries, предпросмотр выражений, назначение обработки/применение шаблона обработки.
/// Часть декомпозиции <see cref="DataSetService"/> (см. архитектурный отчёт, «Предложение 3»).
/// </summary>
public class DataSetSourceService(
    AppDbContext db,
    IBlobStorage blob,
    DataSetParserFactory parserFactory)
{
    private record CachedColumnInfo(string Name, string[] SampleValues);

    // cachedSchema stores camelCase keys ("name"/"sampleValues") — match them case-insensitively.
    private static readonly JsonSerializerOptions CachedSchemaJson = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<DataSetSourceDto>> ListSourcesAsync(Guid fileId, CancellationToken ct)
    {
        var sources = await db.DataSetSources.Where(s => s.FileId == fileId).AsNoTracking().ToListAsync(ct);
        return sources.Select(DataSetDtoMapper.MapSource).ToList();
    }

    /// <summary>
    /// Детект «кандидатов» на источник в сыром файле (листы XLSX, top-level массивы JSON, «весь файл»
    /// для CSV) — БЕЗ персиста. Используется диалогом создания источника как подсказки в один клик.
    /// Для XML парсер кандидатов не даёт (пусто) — источник строится вручную через XPath-builder.
    /// </summary>
    public async Task<IReadOnlyList<DataSetSourceInfo>> DetectSourceCandidatesAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new KeyNotFoundException($"DataSetFile {fileId} not found");

        // PDF (issue #30/#38/#44): кандидаты из СЫРЬЯ набора, дискриминатор — профиль (issue #44).
        if (file.Format == DataSetFormat.Pdf)
        {
            var descriptor = PdfProfileRegistry.ByProfileMarker(file.PreprocessingProfile);
            return descriptor?.Kind == PdfProfileKind.InvoiceFixedSlices
                ? await InvoiceCandidatesAsync(file, ct)
                : await PdfCandidatesAsync(file, ct);
        }

        await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);

        var parser = parserFactory.GetParser(file.Format);
        return await parser.DetectSourcesAsync(ms.ToArray(), ct);
    }

    private async Task<IReadOnlyList<DataSetSourceInfo>> PdfCandidatesAsync(Domain.DataSets.DataSetFile file, CancellationToken ct)
    {
        var grouping = GostGroupingSerialization.Parse(file.Grouping);
        if (grouping is null) return [];
        var projected = GostGroupingProjection.Project(grouping);
        var existing = await db.DataSetSources.Where(s => s.FileId == file.Id).Select(s => s.SheetOrPath).ToListAsync(ct);

        // Кандидаты набора-СЫРЬЯ (issue #38): все проецируются из группировки, создаются пользователем.
        var candidates = new List<DataSetSourceInfo>();
        if (projected.Documents.Count > 0 && !existing.Contains(PdfProfiles.GostDocumentsMarker))
        {
            var docRows = projected.Documents.Select(d => d.Fields).ToList();
            candidates.Add(new DataSetSourceInfo("Документы", PdfProfiles.GostDocumentsMarker, ColumnsFromRows(docRows), docRows.Count));
        }
        if (projected.Cover.Count > 0 && !existing.Contains(PdfProfiles.GostCoverMarker))
            candidates.Add(new DataSetSourceInfo("Обложка", PdfProfiles.GostCoverMarker, ColumnsFromRows(projected.Cover), projected.Cover.Count));
        if (projected.TitlePage.Count > 0 && !existing.Contains(PdfProfiles.GostTitlePageMarker))
            candidates.Add(new DataSetSourceInfo("Титульный лист", PdfProfiles.GostTitlePageMarker, ColumnsFromRows(projected.TitlePage), projected.TitlePage.Count));

        // Таблицы (issue #42): группа-документ с табличным тэгом и распознанным СЫРЬЁМ таблицы (TableData)
        // → кандидат «Таблица …». Источник-проекцию создаёт пользователь (ключ gost-table:{стабильный id}).
        foreach (var g in grouping.Groups)
        {
            if (g.Kind != GostGroupKind.Document || g.Id == Guid.Empty) continue;
            var hasTableTag = (g.Tags ?? []).Any(t => GostTableFields.ColumnsForTag(t) is not null);
            if (!hasTableTag || string.IsNullOrEmpty(g.TableData)) continue;
            var marker = $"{PdfProfiles.GostTableMarkerPrefix}{g.Id}";
            if (existing.Contains(marker)) continue;
            var name = string.IsNullOrWhiteSpace(g.Name) ? "Таблица" : $"Таблица — {g.Name}";
            candidates.Add(new DataSetSourceInfo(name, marker, ColumnsFromSchemaJson(g.TableColumns), RowCountOf(g.TableData)));
        }
        return candidates;
    }

    // Кандидаты профиля «Счёт на оплату» (issue #44) — из СЫРЬЯ набора (InvoiceRawData), тем же
    // паттерном, что и ГОСТ: источники создаёт пользователь, распознавание их не создаёт.
    private async Task<IReadOnlyList<DataSetSourceInfo>> InvoiceCandidatesAsync(Domain.DataSets.DataSetFile file, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(file.InvoiceRawData)) return [];
        var raw = JsonSerializer.Deserialize<InvoiceRawData>(file.InvoiceRawData);
        if (raw is null) return [];
        var existing = await db.DataSetSources.Where(s => s.FileId == file.Id).Select(s => s.SheetOrPath).ToListAsync(ct);

        var candidates = new List<DataSetSourceInfo>();
        if (!existing.Contains(PdfProfiles.InvoiceHeaderMarker))
            candidates.Add(new DataSetSourceInfo("Шапка", PdfProfiles.InvoiceHeaderMarker, ColumnsFromRows([raw.Header]), 1));
        if (raw.LineItems.Count > 0 && !existing.Contains(PdfProfiles.InvoiceLineItemsMarker))
            candidates.Add(new DataSetSourceInfo("Товары", PdfProfiles.InvoiceLineItemsMarker, ColumnsFromRows(raw.LineItems), raw.LineItems.Count));
        return candidates;
    }

    private static IReadOnlyList<DataSetColumnInfo> ColumnsFromRows(IReadOnlyList<Dictionary<string, string?>> rows)
    {
        var names = rows.SelectMany(r => r.Keys).Distinct().ToList();
        return names.Select(n => new DataSetColumnInfo(n,
            rows.Take(3).Select(r => r.GetValueOrDefault(n) ?? "").ToArray())).ToList();
    }

    private static IReadOnlyList<DataSetColumnInfo> ColumnsFromSchemaJson(string? schemaJson)
    {
        var cols = JsonSerializer.Deserialize<CachedColumnInfo[]>(schemaJson ?? "[]", CachedSchemaJson) ?? [];
        return cols.Select(c => new DataSetColumnInfo(c.Name, c.SampleValues)).ToList();
    }

    private static int RowCountOf(string? dataJson)
    {
        try { return JsonSerializer.Deserialize<List<Dictionary<string, string?>>>(dataJson ?? "[]")?.Count ?? 0; }
        catch { return 0; }
    }

    public async Task<SourcePreviewDto?> PreviewSourceAsync(Guid sourceId, int maxRows, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var rows = await DataSetBindingProcessor.LoadRowsAsync(blob, parserFactory, source, ct);

        var take = maxRows <= 0 ? 50 : maxRows;
        // Базовые колонки — из уже сохранённого кэша схемы (тот же парсер заполнил его при
        // создании/обновлении источника), не повторный парсинг: для PDF вообще нет "живого"
        // парсинга (см. LoadRowsAsync), а для остальных форматов результат эквивалентен.
        var baseColumns = JsonSerializer.Deserialize<CachedColumnInfo[]>(source.CachedSchema, CachedSchemaJson) ?? [];
        var columns = baseColumns.Select(c => c.Name).ToList();
        // Вычисляемые колонки могут добавить новые имена, которых нет в исходном разборе.
        columns.AddRange(rows.SelectMany(r => r.Keys).Distinct().Except(columns));

        var previewRows = rows.Take(take)
            .Select(r => (IReadOnlyList<string?>)columns.Select(c => r.TryGetValue(c, out var v) ? v : null).ToList())
            .ToList();
        return new SourcePreviewDto(columns, previewRows, rows.Count);
    }

    public async Task<SourceExportDto?> ExportSourceAsync(Guid sourceId, string? format, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        // Все строки после обработки (Filter/Transformation/Sort) — тот же путь, что и превью, без лимита.
        var rows = await DataSetBindingProcessor.LoadRowsAsync(blob, parserFactory, source, ct);
        var baseColumns = JsonSerializer.Deserialize<CachedColumnInfo[]>(source.CachedSchema, CachedSchemaJson) ?? [];
        var columns = baseColumns.Select(c => c.Name).ToList();
        columns.AddRange(rows.SelectMany(r => r.Keys).Distinct().Except(columns));

        var exportRows = rows
            .Select(r => (IReadOnlyList<string?>)columns.Select(c => r.TryGetValue(c, out var v) ? v : null).ToList())
            .ToList();

        var (bytes, ext, contentType) = SpreadsheetExporter.Export(
            SpreadsheetExporter.ParseFormat(format), columns, exportRows, sheetName: source.Name);
        var fileName = $"{DataSetDtoMapper.SanitizeFileName(source.Name)}.{ext}";
        return new SourceExportDto(bytes, fileName, contentType);
    }

    public async Task<Dictionary<string, string>?> AutoMapAsync(
        Guid sourceId, IReadOnlyList<FieldInfo> fields, CancellationToken ct)
    {
        var source = await db.DataSetSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var columns = JsonSerializer.Deserialize<CachedColumnInfo[]>(source.CachedSchema, CachedSchemaJson) ?? [];
        return DataSetAutoMapper.AutoMap(columns.Select(c => c.Name).ToList(), fields);
    }

    /// <summary>Настроить/снять материализацию источника в тип (issue #19): typeId=null снимает.</summary>
    public async Task<DataSetSourceDto?> SetMaterializationAsync(Guid sourceId, Guid? typeId, Dictionary<string, string>? mapping, CancellationToken ct)
    {
        var source = await db.DataSetSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var mappingJson = typeId is null ? null : JsonSerializer.Serialize(mapping ?? new Dictionary<string, string>());
        source.SetMaterialization(typeId, mappingJson);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    /// <summary>
    /// Предпросмотр материализации: строки источника (после всех обработок) → объекты формы типа по
    /// MaterializeMapping. Ссылочный (@@ref) показывается маркером, файловый (@@file) — объектом-вложением
    /// (тот же рендер, что у превью привязки — см. DataSetDtoMapper.PreviewCell). Без резолва каталога.
    /// </summary>
    public async Task<MaterializePreviewDto?> MaterializePreviewAsync(Guid sourceId, int maxRows, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;
        if (source.MaterializeTypeId is null)
            return new MaterializePreviewDto(null, 0, [], "Материализация не настроена");

        try
        {
            var rows = await DataSetBindingProcessor.LoadRowsAsync(blob, parserFactory, source, ct);
            var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(source.MaterializeMapping ?? "{}") ?? new();
            var take = maxRows <= 0 ? 50 : maxRows;

            var mapped = rows.Take(take).Select(row =>
            {
                var obj = new Dictionary<string, object?>();
                foreach (var (fieldKey, mapVal) in mapping)
                {
                    var v = DataSetDtoMapper.PreviewCell(mapVal, row);
                    if (v is not null) obj[fieldKey] = v;
                }
                return obj;
            }).ToList();

            return new MaterializePreviewDto(source.MaterializeTypeId, rows.Count, mapped, null);
        }
        catch (Exception ex)
        {
            return new MaterializePreviewDto(source.MaterializeTypeId, 0, [], ex.Message);
        }
    }

    public async Task<DataSetSourceDto> CreateSourceAsync(Guid fileId, CreateSourceInput input, CancellationToken ct)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new KeyNotFoundException($"DataSetFile {fileId} not found");

        // PDF (issue #30): источник-проекция (Обложка/Титул) создаётся из распознанной группировки
        // набора — не парсингом блоба. Строки проецируются и кэшируются в CachedData.
        if (file.Format == Domain.DataSets.DataSetFormat.Pdf)
            return await CreatePdfProjectionSourceAsync(file, input.Name.Trim(), input.SheetOrPath.Trim(), ct);

        var columnExpressionsJson = DataSetDtoMapper.SerializeColumnExpressions(input.ColumnExpressions);
        var (schema, rowCount) = await ParseForDefinitionAsync(file.BlobPath, file.Format, input.SheetOrPath, columnExpressionsJson, ct);

        var source = file.AddSource(input.Name.Trim(), input.SheetOrPath.Trim(), DataSetDtoMapper.SerializeSchema(schema), rowCount, columnExpressionsJson);
        // file уже отслеживается (загружен из БД) — новый дочерний источник, добавленный в его
        // коллекцию навигации, EF не распознаёт как Added автоматически (Guid — клиентский ключ,
        // не default-значение), поэтому без явного Add() трекер помечает его Modified и
        // пытается сделать UPDATE несуществующей строки → DbUpdateConcurrencyException.
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    // Источник-проекция PDF (issue #30/#38/#42/#44): обложка/титул/документы/таблица/шапка-счёта/товары-
    // счёта проецируются из СЫРЬЯ набора и кэшируются в CachedData (LoadRowsAsync читает из кэша).
    private async Task<DataSetSourceDto> CreatePdfProjectionSourceAsync(
        Domain.DataSets.DataSetFile file, string name, string marker, CancellationToken ct)
    {
        // Счёт (issue #44): сырьё — InvoiceRawData, не Grouping (ГОСТ-специфичный, непостраничная форма).
        if (marker is PdfProfiles.InvoiceHeaderMarker or PdfProfiles.InvoiceLineItemsMarker)
            return await CreateInvoiceProjectionSourceAsync(file, name, marker, ct);

        var grouping = GostGroupingSerialization.Parse(file.Grouping)
            ?? throw new ArgumentException("Набор ещё не распознан — сначала запустите распознавание.");

        // Таблица (issue #42): проекция распознанного СЫРЬЯ таблицы группы (TableData) + материализация
        // в целевой тип по табличному тэгу. Ключ — стабильный id группы (gost-table:{id}).
        if (marker.StartsWith(PdfProfiles.GostTableMarkerPrefix, StringComparison.Ordinal))
            return await CreateTableProjectionSourceAsync(file, name, marker, grouping, ct);

        var projected = GostGroupingProjection.Project(grouping);
        // Проекция-источник из СЫРЬЯ набора (issue #38): обложка/титул/документы проецируются из
        // группировки. «Документы» несут ФайлПуть/РазмерБайт (под-PDF вырезаны при распознавании).
        var rows = marker == PdfProfiles.GostCoverMarker ? projected.Cover
            : marker == PdfProfiles.GostTitlePageMarker ? projected.TitlePage
            : marker == PdfProfiles.GostDocumentsMarker ? projected.Documents.Select(d => d.Fields).ToList()
            : throw new ArgumentException("Для PDF источник создаётся из кандидата обложки/титула/документов/таблицы.");

        var columns = ColumnsFromRows(rows);
        var source = file.AddSource(name, marker, DataSetDtoMapper.SerializeSchema(columns), rows.Count, null, JsonSerializer.Serialize(rows));
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    private async Task<DataSetSourceDto> CreateTableProjectionSourceAsync(
        Domain.DataSets.DataSetFile file, string name, string marker, GostGroupingData grouping, CancellationToken ct)
    {
        var idStr = marker[PdfProfiles.GostTableMarkerPrefix.Length..];
        if (!Guid.TryParse(idStr, out var gid))
            throw new ArgumentException("Некорректный маркер таблицы.");
        var group = grouping.Groups.FirstOrDefault(g => g.Id == gid && g.Kind == GostGroupKind.Document)
            ?? throw new ArgumentException("Документ таблицы не найден в группировке.");
        if (string.IsNullOrEmpty(group.TableData))
            throw new ArgumentException("Таблица ещё не распознана — распознайте её в редакторе разбиения.");

        var source = file.AddSource(name, marker, group.TableColumns ?? "[]", RowCountOf(group.TableData), null, group.TableData);
        // Материализация в целевой тип по табличному тэгу (issue #29/#19): строки распознаны прямо в ключи
        // полей типа, поэтому маппинг тождественный (колонка→одноимённое поле).
        var tag = (group.Tags ?? []).FirstOrDefault(t => GostTableFields.ColumnsForTag(t) is not null);
        if (tag is not null)
        {
            var allTypes = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);
            var targetType = allTypes.FirstOrDefault(t => SchemaTags.TypeHasTag(t, allTypes, tag));
            if (targetType is not null)
            {
                var cols = JsonSerializer.Deserialize<CachedColumnInfo[]>(group.TableColumns ?? "[]", CachedSchemaJson) ?? [];
                source.SetMaterialization(targetType.Id, JsonSerializer.Serialize(cols.ToDictionary(c => c.Name, c => c.Name)));
            }
        }
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    // Источник-проекция «Шапка»/«Товары» профиля «Счёт на оплату» (issue #44) — из СЫРЬЯ набора
    // (InvoiceRawData), тем же паттерном, что Обложка/Титул у ГОСТ.
    private async Task<DataSetSourceDto> CreateInvoiceProjectionSourceAsync(
        Domain.DataSets.DataSetFile file, string name, string marker, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(file.InvoiceRawData))
            throw new ArgumentException("Набор ещё не распознан — сначала запустите распознавание.");
        var raw = JsonSerializer.Deserialize<InvoiceRawData>(file.InvoiceRawData)
            ?? throw new ArgumentException("Не удалось прочитать распознанные данные счёта.");

        IReadOnlyList<Dictionary<string, string?>> rows = marker == PdfProfiles.InvoiceHeaderMarker
            ? [raw.Header]
            : raw.LineItems;

        var columns = ColumnsFromRows(rows);
        var source = file.AddSource(name, marker, DataSetDtoMapper.SerializeSchema(columns), rows.Count, null, JsonSerializer.Serialize(rows));
        db.DataSetSources.Add(source);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    // Лёгкое переименование (issue #43) — только имя, без парсинга/кэша; применимо к любому источнику
    // (включая PDF-проекции, для которых полное UpdateSource недоступно).
    public async Task<DataSetSourceDto?> RenameSourceAsync(Guid sourceId, string name, CancellationToken ct)
    {
        var source = await db.DataSetSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Укажите название.");
        source.Rename(name);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    public async Task<DataSetSourceDto?> UpdateSourceAsync(Guid sourceId, UpdateSourceInput input, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var columnExpressionsJson = DataSetDtoMapper.SerializeColumnExpressions(input.ColumnExpressions);
        var (schema, rowCount) = await ParseForDefinitionAsync(
            source.File.BlobPath, source.File.Format, input.SheetOrPath, columnExpressionsJson, ct);

        source.UpdateDefinition(input.Name.Trim(), input.SheetOrPath.Trim(), columnExpressionsJson);
        source.UpdateCache(DataSetDtoMapper.SerializeSchema(schema), rowCount);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    public async Task<bool> DeleteSourceAsync(Guid sourceId, CancellationToken ct)
    {
        var source = await db.DataSetSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return false;

        var bindings = await db.DataSetBindings.Where(b => b.SourceId == sourceId).ToListAsync(ct);
        if (bindings.Count > 0)
        {
            var usages = await DescribeBindingUsagesAsync(bindings, ct);
            throw new InvalidOperationException(
                $"Источник используется в привязках: {string.Join("; ", usages)} — сначала удалите привязки.");
        }

        db.DataSetSources.Remove(source);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public Task<bool> AnySourceMaterializedAsTypeAsync(Guid documentTypeId, CancellationToken ct) =>
        db.DataSetSources.AnyAsync(s => s.MaterializeTypeId == documentTypeId, ct);

    // Человекочитаемое описание, где именно используется источник (для сообщения об ошибке
    // удаления) — по владельцу-объекту: документ (есть фасета, живёт в комплекте) или запись общих данных.
    private async Task<List<string>> DescribeBindingUsagesAsync(List<DataSetBinding> bindings, CancellationToken ct)
    {
        var usages = new List<string>();
        var ownerIds = bindings.Select(b => b.OwnerId).Distinct().ToList();
        if (ownerIds.Count == 0) return usages;

        var owners = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)
            .Where(o => ownerIds.Contains(o.Id)).ToListAsync(ct);
        var typeIds = owners.Select(o => o.CompositeTypeId).Distinct().ToList();
        var typeNames = await db.DocumentTypes.Where(t => typeIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var setIds = owners.Where(o => o.IsDocument && o.ScopeId != null).Select(o => o.ScopeId!.Value).Distinct().ToList();
        var setNames = await db.DocumentSets.Where(s => setIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        foreach (var o in owners)
        {
            var label = o.DisplayName ?? typeNames.GetValueOrDefault(o.CompositeTypeId, o.IsDocument ? "документ" : "запись");
            if (o.IsDocument)
            {
                var setName = o.ScopeId is { } sid ? setNames.GetValueOrDefault(sid) : null;
                usages.Add(setName is not null ? $"документ «{label}» (комплект «{setName}»)" : $"документ «{label}»");
            }
            else usages.Add($"запись каталога «{label}»");
        }
        return usages;
    }

    // Копия источника на том же файле — тот же locator/колонки/обработка (Filter/Transformation/Sort),
    // но независимая: правки одной копии не затрагивают другую. Позволяет получить несколько
    // наборов на основе одного файла без переопределения extraction с нуля (актуально и для
    // форматов без ручного builder'а — CSV/XLSX — где нужно только разное Filter/Transformation/Sort
    // поверх одинаковых данных).
    public async Task<DataSetSourceDto?> DuplicateSourceAsync(Guid sourceId, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var copy = source.File.AddSource(
            $"{source.Name} (копия)", source.SheetOrPath, source.CachedSchema, source.CachedRowCount,
            source.ColumnExpressions, source.CachedData);
        copy.SetProcessing(source.RowFilter, source.ComputedColumns, source.SortSpec);
        copy.SetTags(source.Tags);
        // file уже отслеживается — см. пояснение в CreateSourceAsync (иначе Modified вместо Added).
        db.DataSetSources.Add(copy);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(copy);
    }

    // Скачивает файл и парсит указанное определение — используется для валидации и первичного
    // расчёта кэша при ручном создании/редактировании источника (в первую очередь для XML).
    private async Task<(IReadOnlyList<DataSetColumnInfo> Schema, int RowCount)> ParseForDefinitionAsync(
        string blobPath, DataSetFormat format, string sheetOrPath, string? columnExpressionsJson, CancellationToken ct)
    {
        await using var stream = await blob.DownloadAsync(blobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);

        var parser = parserFactory.GetParser(format);
        try
        {
            var result = await parser.ParseAsync(ms.ToArray(), sheetOrPath, columnExpressionsJson, ct);
            return (result.Columns, result.Rows.Count);
        }
        catch (Exception ex) when (ex is System.Xml.XPath.XPathException or ArgumentException
            or System.Xml.XmlException or InvalidOperationException or JsonCons.JsonPath.JsonPathParseException)
        {
            throw new ArgumentException($"Не удалось разобрать выражение: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<string>> ListZipXmlEntriesAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new KeyNotFoundException($"DataSetFile {fileId} not found");
        if (file.Format != DataSetFormat.Zip) return [];

        await using var stream = await blob.DownloadAsync(file.BlobPath, ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);

        using var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read, leaveOpen: false);
        return zip.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name) && ZipDataSetParser.DetectEntryFormat(e.FullName) == DataSetFormat.Xml)
            .Select(e => e.FullName)
            .OrderBy(p => p)
            .ToList();
    }

    public async Task<ExpressionPreviewDto> PreviewExpressionAsync(Guid fileId, string rowSelector, string? expr, CancellationToken ct)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new KeyNotFoundException($"DataSetFile {fileId} not found");

        // expr задан — предпросмотр относительного значения колонки (первые строки).
        // expr пуст — предпросмотр самого row-selector'а: сколько узлов и какие у них поля.
        var columnExpressionsJson = !string.IsNullOrWhiteSpace(expr)
            ? JsonSerializer.Serialize(new[] { new { name = "value", expr } })
            : null;

        var (schema, rowCount) = await ParseForDefinitionAsync(file.BlobPath, file.Format, rowSelector, columnExpressionsJson, ct);

        var samples = !string.IsNullOrWhiteSpace(expr)
            ? (IReadOnlyList<string>)(schema.FirstOrDefault()?.SampleValues.ToList() ?? [])
            : schema.Select(c => $"{c.Name}: {string.Join(", ", c.SampleValues)}").ToList();

        return new ExpressionPreviewDto(rowCount, samples);
    }

    public async Task<DataSetSourceDto?> SetSourceProcessingAsync(Guid sourceId, SetSourceProcessingInput input, CancellationToken ct)
    {
        var source = await db.DataSetSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        source.SetProcessing(
            DataSetDtoMapper.SerializeJson(input.RowFilter), DataSetDtoMapper.SerializeJson(input.ComputedColumns), DataSetDtoMapper.SerializeJson(input.SortSpec));
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    public async Task<DataSetSourceDto?> ApplyProcessingTemplateAsync(Guid sourceId, Guid templateId, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var template = await db.DataSetProcessingTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, ct)
            ?? throw new KeyNotFoundException($"DataSetProcessingTemplate {templateId} not found");

        // Extraction в шаблоне — опциональна: если задана, пере-парсим файл (имя источника не
        // трогаем — оно своё у каждого источника, не часть рецепта).
        if (!string.IsNullOrWhiteSpace(template.SheetOrPath))
        {
            var (schema, rowCount) = await ParseForDefinitionAsync(
                source.File.BlobPath, source.File.Format, template.SheetOrPath, template.ColumnExpressions, ct);
            source.UpdateDefinition(source.Name, template.SheetOrPath, template.ColumnExpressions);
            source.UpdateCache(DataSetDtoMapper.SerializeSchema(schema), rowCount);
        }
        source.SetProcessing(template.RowFilter, template.ComputedColumns, template.SortSpec);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }
}
