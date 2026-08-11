using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
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
    DataSetParserFactory parserFactory,
    IDataSetRowLoader rowLoader,
    SystemDataProviderRegistry systemProviders,
    SystemSourceCounter systemCounts,
    IRecognitionProfileProvider profiles)
{
    private record CachedColumnInfo(string Name, string[] SampleValues);

    // cachedSchema stores camelCase keys ("name"/"sampleValues") — match them case-insensitively.
    private static readonly JsonSerializerOptions CachedSchemaJson = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<DataSetSourceDto>> ListSourcesAsync(Guid fileId, CancellationToken ct)
    {
        var sources = await db.DataSetSources.Where(s => s.FileId == fileId).AsNoTracking().ToListAsync(ct);
        var ids = sources.Select(s => s.Id).ToList();
        var bindingCounts = ids.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.DataSetBindings.AsNoTracking()
                .Where(b => ids.Contains(b.SourceId))
                .GroupBy(b => b.SourceId)
                .ToDictionaryAsync(g => g.Key, g => g.Count(), ct);

        // Системный набор: число строк живое (issue #613) — файл не запрашиваем, если нечего считать.
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct);
        var liveStates = file is null
            ? new Dictionary<Guid, SystemSourceCounter.SystemSourceState>()
            : await systemCounts.StateAsync(file, sources, ct);

        return sources.Select(s => DataSetDtoMapper.MapSource(
            s, bindingCounts.GetValueOrDefault(s.Id),
            liveStates.TryGetValue(s.Id, out var live) ? live : null)).ToList();
    }

    /// <summary>
    /// Какие консолидации данных системы возможны на уровне — ДО создания набора (issue #606).
    /// Нужно, чтобы не предлагать системный набор там, где предложить нечего: «Документы комплекта»
    /// осмысленны только внутри комплекта, и на уровне раздела пользователь иначе упирался бы в
    /// пустой список источников.
    /// </summary>
    public async Task<IReadOnlyList<DataSetSourceInfo>> ListSystemCandidatesAsync(
        CatalogScope scope, Guid? scopeId, CancellationToken ct)
    {
        var candidates = new List<DataSetSourceInfo>();
        foreach (var provider in systemProviders.All)
            candidates.AddRange(await provider.GetCandidatesAsync(scope, scopeId, ct));
        return candidates;
    }

    /// <summary>
    /// Детект «кандидатов» на источник в сыром файле (листы XLSX, top-level массивы JSON, «весь файл»
    /// для CSV) — БЕЗ персиста. Используется диалогом создания источника как подсказки в один клик.
    /// Для XML парсер кандидатов не даёт (пусто) — источник строится вручную через XPath-builder.
    /// </summary>
    public async Task<IReadOnlyList<DataSetSourceInfo>> DetectSourceCandidatesAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new NotFoundException($"DataSetFile {fileId} not found");

        // Системный набор (issue #580): кандидаты — консолидации, возможные на уровне набора.
        // Занятую консолидацию НЕ прячем (issue #717): один и тот же список документов законно
        // нужен дважды — с разными фильтрами и в разные поля, — а исчезнувший кандидат оставлял
        // единственным входом «Создать копию» в меню строки, куда за этим никто не пойдёт.
        // Вместо изъятия отдаём счётчик: кандидат виден, и по нему видно, что он уже добавлен.
        if (file.Format == DataSetFormat.System)
        {
            var existingMarkers = await db.DataSetSources
                .Where(s => s.FileId == file.Id).Select(s => s.SheetOrPath).ToListAsync(ct);
            var candidates = new List<DataSetSourceInfo>();
            foreach (var provider in systemProviders.All)
                candidates.AddRange((await provider.GetCandidatesAsync(file.Scope, file.ScopeId, ct))
                    .Select(c => c with { ExistingCount = existingMarkers.Count(m => m == c.SheetOrPath) }));
            return candidates;
        }

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
            // Табличность — общий предикат (issue #410): привязан профиль ИЛИ есть табличный тэг.
            // Проверка только по тэгу скрывала бы кандидата произвольной таблицы.
            if (!await profiles.IsTableGroupAsync(g.ProfileId, g.Tags, ct)) continue;
            var marker = $"{PdfProfiles.GostTableMarkerPrefix}{g.Id}";
            if (existing.Contains(marker)) continue;
            var name = string.IsNullOrWhiteSpace(g.Name) ? "Таблица" : $"Таблица — {g.Name}";
            if (!string.IsNullOrEmpty(g.TableData))
            {
                // Таблица распознана → готовый кандидат (создаётся сразу).
                candidates.Add(new DataSetSourceInfo(name, marker, ColumnsFromSchemaJson(g.TableColumns), RowCountOf(g.TableData)));
            }
            else
            {
                // Таблица ещё НЕ распознана (issue #385): кандидат с FirstPageIndex — фронт покажет
                // «Распознать таблицу»; после распознавания станет обычным готовым кандидатом.
                var firstPage = g.Pages.Count > 0 ? g.Pages.Min(p => p.PageIndex) : 0;
                candidates.Add(new DataSetSourceInfo(name, marker, [], 0, firstPage));
            }
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

        var loaded = await rowLoader.LoadAsync(source, ct);
        var rows = loaded.Rows;

        var take = maxRows <= 0 ? 50 : maxRows;
        // Базовые колонки — те, что источник отдал на этой же загрузке, иначе из сохранённого кэша
        // схемы (тот же парсер заполнил его при создании/обновлении источника). У системного
        // источника кэш обновить нечем (issue #664), и убранное из схемы типа поле осталось бы здесь
        // колонкой-призраком из пустых ячеек; у PDF живого разбора нет вовсе — там кэш и есть истина.
        var columns = BaseColumnNames(loaded.Columns, source.CachedSchema);
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
        var loaded = await rowLoader.LoadAsync(source, ct);
        var rows = loaded.Rows;
        var columns = BaseColumnNames(loaded.Columns, source.CachedSchema);
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
        var source = await db.DataSetSources.Include(s => s.File).AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source?.File is null) return null;

        // Колонки системного источника живые (issue #664): предложить маппинг на колонку, которой в
        // строках уже нет, значит записать привязку, молча дающую пустое значение при генерации.
        //
        // Цена честно: провайдер собирает консолидацию ЦЕЛИКОМ — колонки без строк он отдавать не
        // умеет, дешёвого способа спросить только состав в контракте нет (осознанно, issue #622).
        // Здесь это терпимо: действие разовое и ручное, в отличие от списков наборов, где тот же
        // вызов идёт на каждый источник. Понадобится дешевле — заводить `ColumnsAsync` в контракте
        // провайдера, а не обходить его здесь.
        var live = await systemCounts.StateAsync(source, source.File, ct);
        return DataSetAutoMapper.AutoMap(BaseColumnNames(live?.Columns, source.CachedSchema), fields);
    }

    /// <summary>
    /// Колонки источника: живые, если он их отдал (системная консолидация — issue #664), иначе из
    /// кэша схемы. Пустой живой список — тоже «отдать кэш»: описание, стёртое в ноль, хуже
    /// устаревшего.
    /// </summary>
    private static List<string> BaseColumnNames(IReadOnlyList<DataSetColumnInfo>? live, string cachedSchema) =>
        live is { Count: > 0 }
            ? [.. live.Select(c => c.Name)]
            : [.. (JsonSerializer.Deserialize<CachedColumnInfo[]>(cachedSchema, CachedSchemaJson) ?? [])
                .Select(c => c.Name)];

    /// <summary>
    /// Настроить/снять материализацию источника в тип (issue #19): typeId=null снимает. Настройка
    /// задаётся целиком: тип, маппинг и (issue #716) правило выбора варианта.
    /// Сохраняется ЗАМЕЩЕНИЕМ — частичных правок здесь нет намеренно: маппинг и правила связаны, и
    /// сохранить одно без другого значит оставить источник в состоянии, которого валидатор не пропустил бы.
    /// </summary>
    public async Task<DataSetSourceDto?> SetMaterializationAsync(
        Guid sourceId, Guid? typeId, Dictionary<string, string>? mapping,
        MaterializeDiscriminatorConfig? discriminator, string? byIdColumn, CancellationToken ct)
    {
        var source = await db.DataSetSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var effectiveMapping = mapping ?? new Dictionary<string, string>();
        if (typeId is { } id)
        {
            var typesById = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
            if (!typesById.TryGetValue(id, out var type))
                throw new NotFoundException($"Тип {id} не найден.");
            MaterializeConfigValidator.Validate(type, effectiveMapping, discriminator, typesById, byIdColumn);
        }

        var mappingJson = typeId is null ? null : JsonSerializer.Serialize(effectiveMapping);
        var discriminatorJson = typeId is null || discriminator is null
            ? null
            : JsonSerializer.Serialize(discriminator);
        source.SetMaterialization(typeId, mappingJson, discriminatorJson, byIdColumn);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    /// <summary>
    /// Предпросмотр материализации: строки источника (после всех обработок) → объекты формы типа по
    /// MaterializeMapping. Ссылочный (@@ref) показывается маркером, файловый (@@file) — объектом-вложением
    /// (тот же рендер, что у превью привязки — см. DataSetDtoMapper.PreviewCell). Без резолва каталога.
    /// </summary>
    public async Task<MaterializePreviewDto?> MaterializePreviewAsync(
        Guid sourceId, int maxRows, Guid? typeId, Dictionary<string, string>? mapping,
        MaterializeDiscriminatorConfig? discriminator, string? byIdColumn, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;
        // typeId/mapping переданы диалогом (live-превью несохранённой настройки, issue #294) — иначе
        // сохранённые на источнике. typeId служит лишь маркером «есть что материализовать».
        var effTypeId = typeId ?? source.MaterializeTypeId;
        if (effTypeId is null)
            return new MaterializePreviewDto(null, 0, [], "Материализация не настроена");
        var effMapping = mapping ?? JsonSerializer.Deserialize<Dictionary<string, string>>(source.MaterializeMapping ?? "{}") ?? new();
        // Правило варианта — тоже живое (issue #294, #716). Но «пусто» здесь не может значить
        // «взять сохранённое»: диалог обязан уметь показать предпросмотр БЕЗ правила — ровно это
        // происходит при переключении в режим «один вариант на все строки». Иначе предпросмотр
        // отвечал бы по вчерашней настройке именно тогда, когда её меняют.
        //
        // Признак «диалог ведёт настройку» — переданный маппинг: он приходит вместе с правилом или
        // не приходит вовсе. Отдельного флага не заводим, чтобы не было двух способов сказать одно.
        var effDiscriminator = mapping is not null
            ? discriminator
            : MaterializeVariantSelector.ParseConfig(source.MaterializeDiscriminator);
        // Колонка режима «по Ид» (issue #725) — живая по тому же признаку: диалог, ведущий настройку,
        // присылает маппинг, и переключение режима обязано быть видно в предпросмотре сразу.
        var effByIdColumn = mapping is not null ? byIdColumn : source.MaterializeByIdColumn;

        try
        {
            var rows = await rowLoader.LoadRowsAsync(source, ct);
            var take = maxRows <= 0 ? 50 : maxRows;
            var page = rows.Take(take).ToList();

            if (MaterializeByIdMode.IsOn(effByIdColumn))
                return await ByIdPreviewAsync(effTypeId, effByIdColumn!, page, rows.Count, SetOf(source.File), ct);

            MaterializeVariantSelector? selector = null;
            if (effDiscriminator is not null && !string.IsNullOrWhiteSpace(effDiscriminator.Column))
            {
                var typesById = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
                Dictionary<Guid, Guid>? typeByDocument = null;
                var documentIds = MaterializeVariantSelector.DocumentIdsIn(effDiscriminator, page).ToList();
                if (documentIds.Count > 0)
                    typeByDocument = await db.DomainObjects.AsNoTracking()
                        .Where(o => documentIds.Contains(o.Id))
                        .ToDictionaryAsync(o => o.Id, o => o.CompositeTypeId, ct);
                selector = MaterializeVariantSelector.Create(effDiscriminator, typesById, typeByDocument);
            }

            var mapped = new List<Dictionary<string, object?>>();
            var variants = new List<string?>();
            var skipped = new List<MaterializeSkippedRowDto>();
            var rowIndex = 0;

            foreach (var row in page)
            {
                rowIndex++;
                var variantKey = (string?)null;
                var pairs = effMapping.AsEnumerable();

                if (selector is not null)
                {
                    var choice = selector.Choose(row);
                    if (choice.VariantKey is null)
                    {
                        // Пропущенные строки перечисляем ПОИМЁННО, а не числом: предпросмотр для того
                        // и открывают — понять, какие именно документы не доехали и почему. Сводку
                        // числом даёт генерация, ей построчный список ни к чему.
                        skipped.Add(new MaterializeSkippedRowDto(
                            rowIndex,
                            row.TryGetValue(effDiscriminator!.Column, out var cell) ? cell : null,
                            choice.SkipReason ?? "",
                            MaterializeSkipReason.Describe(choice.SkipReason ?? "")));
                        continue;
                    }
                    variantKey = choice.VariantKey;
                    pairs = effMapping.Where(p => p.Key == choice.VariantKey);
                }

                var obj = new Dictionary<string, object?>();
                foreach (var (fieldKey, mapVal) in pairs)
                {
                    var v = await DataSetDtoMapper.PreviewCellAsync(mapVal, row, ct);
                    if (v is not null) obj[fieldKey] = v;
                }
                mapped.Add(obj);
                variants.Add(variantKey);
            }

            // Ссылки на документы — наименованиями, а не идентификаторами (issue #715, пункт проверки,
            // и issue #725). Пока превью отдавало сырой GUID, «ссылка на удалённый документ» выглядела
            // здесь ровно так же, как рабочая, — и расходились они только при генерации.
            await DocRefPreviewLabeler.LabelAsync(db, mapped, effTypeId, SetOf(source.File), ct);

            return new MaterializePreviewDto(effTypeId, rows.Count, mapped, null, variants, skipped);
        }
        catch (Exception ex)
        {
            return new MaterializePreviewDto(effTypeId, 0, [], ex.Message);
        }
    }

    /// <summary>
    /// Предпросмотр режима «существующий документ по Ид» (issue #725): строка = один документ,
    /// показанный наименованием; отсутствующий по идентификатору назван отсутствующим. Строки без
    /// идентификатора перечисляются поимённо — как и пропуски дискриминатора, и ровно затем же.
    /// </summary>
    private async Task<MaterializePreviewDto> ByIdPreviewAsync(
        Guid? typeId, string column,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> page, int totalRows, Guid? setId, CancellationToken ct)
    {
        var ids = new List<Guid>();
        var skipped = new List<MaterializeSkippedRowDto>();
        var rowIndex = 0;

        foreach (var row in page)
        {
            rowIndex++;
            var (id, reason, cell) = MaterializeByIdMode.ReadId(row, column);
            if (id is null)
                skipped.Add(new MaterializeSkippedRowDto(
                    rowIndex, cell, reason!, MaterializeSkipReason.Describe(reason!)));
            else ids.Add(id.Value);
        }

        var labels = await MaterializeByIdMode.ResolveLabelsAsync(db, [.. ids.Distinct()], setId, ct);
        var rows = ids
            .Select(id => new Dictionary<string, object?>
            {
                ["Документ"] = labels.TryGetValue(id, out var label) ? label : MaterializeByIdMode.NotFoundLabel,
            })
            .ToList();

        return new MaterializePreviewDto(typeId, totalRows, rows, null, null, skipped);
    }

    /// <summary>Комплект, в котором будут разворачиваться ссылки строк этого набора; null — набор
    /// живёт выше комплекта и используется в разных, и проверять принадлежность нечему.</summary>
    private static Guid? SetOf(DataSetFile file) => file.Scope == CatalogScope.Set ? file.ScopeId : null;

    public async Task<DataSetSourceDto> CreateSourceAsync(Guid fileId, CreateSourceInput input, CancellationToken ct)
    {
        var file = await db.DataSetFiles.Include(f => f.Sources).FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new NotFoundException($"DataSetFile {fileId} not found");
        if (string.IsNullOrWhiteSpace(input.Name)) throw new InvalidRequestException("Укажите название источника.");
        await EnsureNameFreeAsync(fileId, input.Name.Trim(), null, ct);

        // PDF (issue #30): источник-проекция (Обложка/Титул) создаётся из распознанной группировки
        // набора — не парсингом блоба. Строки проецируются и кэшируются в CachedData.
        if (file.Format == Domain.DataSets.DataSetFormat.Pdf)
            return await CreatePdfProjectionSourceAsync(file, input.Name.Trim(), input.SheetOrPath.Trim(), ct);

        // Системный набор (issue #580): строки даёт провайдер, кэшировать их нельзя (данные живые) —
        // прогон нужен только чтобы записать схему колонок и счётчик строк для UI.
        if (file.Format == Domain.DataSets.DataSetFormat.System)
            return await CreateSystemSourceAsync(file, input.Name.Trim(), input.SheetOrPath.Trim(), ct);

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

    // Источник системного набора (issue #580): маркер выбирает провайдера консолидации. CachedData не
    // пишем — строки собираются заново при каждом обращении, иначе реестр отстанет от состава комплекта.
    private async Task<DataSetSourceDto> CreateSystemSourceAsync(
        Domain.DataSets.DataSetFile file, string name, string marker, CancellationToken ct)
    {
        var provider = systemProviders.Get(marker);
        var provided = await provider.ProvideAsync(marker, file.Scope, file.ScopeId, ct);

        var source = file.AddSource(name, marker, DataSetDtoMapper.SerializeSchema(provided.Columns), provided.Rows.Count);
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
            ?? throw new InvalidRequestException("Набор ещё не распознан — сначала запустите распознавание.");

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
            : throw new InvalidRequestException("Для PDF источник создаётся из кандидата обложки/титула/документов/таблицы.");

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
            throw new InvalidRequestException("Некорректный маркер таблицы.");
        var group = grouping.Groups.FirstOrDefault(g => g.Id == gid && g.Kind == GostGroupKind.Document)
            ?? throw new InvalidRequestException("Документ таблицы не найден в группировке.");
        if (string.IsNullOrEmpty(group.TableData))
            throw new InvalidRequestException("Таблица ещё не распознана — распознайте её в редакторе разбиения.");

        var source = file.AddSource(name, marker, group.TableColumns ?? "[]", RowCountOf(group.TableData), null, group.TableData);
        // Материализация в целевой тип по табличному тэгу (issue #29/#19): строки распознаны прямо в ключи
        // полей типа, поэтому маппинг тождественный (колонка→одноимённое поле).
        var tag = (group.Tags ?? []).FirstOrDefault(profiles.IsTableTag);
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
            throw new InvalidRequestException("Набор ещё не распознан — сначала запустите распознавание.");
        var raw = JsonSerializer.Deserialize<InvoiceRawData>(file.InvoiceRawData)
            ?? throw new InvalidRequestException("Не удалось прочитать распознанные данные счёта.");

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
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidRequestException("Укажите название.");
        await EnsureNameFreeAsync(source.FileId, name.Trim(), sourceId, ct, source.Name);
        source.Rename(name);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(source);
    }

    public async Task<DataSetSourceDto?> UpdateSourceAsync(Guid sourceId, UpdateSourceInput input, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;
        // Определение системного источника — это выбор консолидации, менять в нём нечего: переименование
        // идёт через RenameSourceAsync, а другая консолидация — другой источник.
        if (source.File.IsSystem)
            throw new InvalidRequestException("Определение системного источника не редактируется — переименуйте его или создайте другой.");
        if (string.IsNullOrWhiteSpace(input.Name)) throw new InvalidRequestException("Укажите название источника.");
        await EnsureNameFreeAsync(source.FileId, input.Name.Trim(), sourceId, ct, source.Name);

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
            throw new ConflictException(
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
    //
    // Материализация копируется вместе с остальным (issue #717): копия — «тот же источник, другой
    // фильтр», и настраивать тип, маппинг и правило выбора варианта заново значит потерять работу,
    // ради которой копию и делают. Имя задаёт вызывающий (диалог), иначе берём ближайшее свободное.
    public async Task<DataSetSourceDto?> DuplicateSourceAsync(Guid sourceId, string? name, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File).FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null) return null;

        var copyName = string.IsNullOrWhiteSpace(name)
            ? SourceNaming.Next(await SiblingNamesAsync(source.FileId, null, ct), source.Name)
            : name.Trim();
        await EnsureNameFreeAsync(source.FileId, copyName, null, ct);

        var copy = source.File.AddSource(
            copyName, source.SheetOrPath, source.CachedSchema, source.CachedRowCount,
            source.ColumnExpressions, source.CachedData);
        copy.SetProcessing(source.RowFilter, source.ComputedColumns, source.SortSpec);
        copy.SetTags(source.Tags);
        copy.SetMaterialization(source.MaterializeTypeId, source.MaterializeMapping, source.MaterializeDiscriminator,
            source.MaterializeByIdColumn);
        // file уже отслеживается — см. пояснение в CreateSourceAsync (иначе Modified вместо Added).
        db.DataSetSources.Add(copy);
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapSource(copy);
    }

    private Task<List<string>> SiblingNamesAsync(Guid fileId, Guid? exceptSourceId, CancellationToken ct) =>
        db.DataSetSources.AsNoTracking()
            .Where(s => s.FileId == fileId && (exceptSourceId == null || s.Id != exceptSourceId))
            .Select(s => s.Name).ToListAsync(ct);

    // Одинаковые имена внутри набора запрещены (issue #717): см. пояснение в SourceNaming — в
    // селекторе привязки источники различимы только именем. Проверка стоит на ВСЕХ путях записи
    // имени (создание, копия, переименование, правка определения), а не только в диалоге копии:
    // переименовать второй источник в имя первого — та же неразличимость, только позже.
    //
    // currentName — имя, которое источник носит сейчас: если оно не меняется, проверять нечего.
    // До этого правила имена не были уникальны, и наборы прошлых версий вполне могут содержать два
    // «Лист1». Без этой оговорки правка row-selector'а такому источнику отказывала бы ссылкой на
    // название, которого пользователь не трогал, и выйти можно было бы только переименованием.
    private async Task EnsureNameFreeAsync(
        Guid fileId, string name, Guid? exceptSourceId, CancellationToken ct, string? currentName = null)
    {
        if (currentName is not null && string.Equals(currentName.Trim(), name, StringComparison.OrdinalIgnoreCase))
            return;

        var taken = await SiblingNamesAsync(fileId, exceptSourceId, ct);
        if (taken.Any(n => string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidRequestException(
                $"Источник «{name}» в этом наборе уже есть — дайте другое название (или переименуйте тот), "
                + "иначе их не различить при привязке.");
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
            throw new InvalidRequestException($"Не удалось разобрать выражение: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<string>> ListZipXmlEntriesAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.DataSetFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct)
            ?? throw new NotFoundException($"DataSetFile {fileId} not found");
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
            ?? throw new NotFoundException($"DataSetFile {fileId} not found");

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
            ?? throw new NotFoundException($"DataSetProcessingTemplate {templateId} not found");

        // Extraction в шаблоне — опциональна: если задана, пере-парсим файл (имя источника не
        // трогаем — оно своё у каждого источника, не часть рецепта). У системного источника
        // extraction — это ВЫБОР КОНСОЛИДАЦИИ, а не лист файла: подменять его рецептом нельзя
        // (парсера у формата System нет — прежде здесь падало «Нет парсера для формата System»).
        // Обработку при этом переносим: фильтр/колонки/сортировка к живым строкам применимы (#613).
        if (!string.IsNullOrWhiteSpace(template.SheetOrPath) && !source.File.IsSystem)
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
