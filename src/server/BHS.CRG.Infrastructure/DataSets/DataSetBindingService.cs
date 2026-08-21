using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Привязки набора данных к документу/записи каталога (CRUD + предпросмотр значений).
/// Часть декомпозиции <see cref="DataSetService"/> (см. архитектурный отчёт, «Предложение 3»).
/// </summary>
public class DataSetBindingService(
    AppDbContext db,
    IDataSetRowLoader rowLoader,
    ILogger<DataSetBindingService> logger)
{
    public async Task<IReadOnlyList<DataSetBindingDto>> ListBindingsAsync(Guid ownerId, CancellationToken ct)
    {
        var bindings = await db.DataSetBindings
            .Include(b => b.Source).ThenInclude(s => s.File)
            .Where(b => b.OwnerId == ownerId)
            .AsNoTracking()
            .ToListAsync(ct);
        var counts = await BindingCountsAsync(bindings, ct);
        return bindings.Select(b => DataSetDtoMapper.MapBinding(b, counts)).ToList();
    }

    /// <inheritdoc cref="Application.DataSets.IDataSetService.ListBindingsForOwnersAsync" />
    public async Task<IReadOnlyList<DataSetBindingDto>> ListBindingsForOwnersAsync(
        IReadOnlyCollection<Guid> ownerIds, CancellationToken ct)
    {
        if (ownerIds.Count == 0) return [];
        var bindings = await db.DataSetBindings
            .Include(b => b.Source).ThenInclude(s => s.File)
            .Where(b => ownerIds.Contains(b.OwnerId))
            .AsNoTracking()
            .ToListAsync(ct);
        var counts = await BindingCountsAsync(bindings, ct);
        return bindings.Select(b => DataSetDtoMapper.MapBinding(b, counts)).ToList();
    }

    /// <summary>Сколько привязок ссылается на каждый из встреченных источников (issue #815) — одним
    /// запросом на список, а не по разу на привязку. Нужно подтверждению перед перераспознаванием:
    /// человек жмёт кнопку в своём документе и не ждёт, что тронет данные в чужих.</summary>
    private async Task<Dictionary<Guid, int>> BindingCountsAsync(
        IReadOnlyCollection<DataSetBinding> bindings, CancellationToken ct)
    {
        // Считаем только по источникам, которые перераспознают: у прочих число никому не показывают.
        var sourceIds = bindings.Where(b => b.Source?.RecognitionStale == true)
            .Select(b => b.SourceId).Distinct().ToList();
        if (sourceIds.Count == 0) return [];
        return await db.DataSetBindings.AsNoTracking()
            .Where(b => sourceIds.Contains(b.SourceId))
            .GroupBy(b => b.SourceId)
            .Select(g => new { SourceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SourceId, x => x.Count, ct);
    }

    /// <inheritdoc cref="Application.DataSets.IDataSetService.MigrateFieldKeyAsync" />
    public async Task<BindingKeyMigrationResult> MigrateFieldKeyAsync(
        IReadOnlyCollection<Guid> ownerIds, IReadOnlyCollection<Guid> documentTypeIds,
        string oldKey, string newKey, CancellationToken ct)
    {
        var bindings = ownerIds.Count == 0
            ? []
            : await db.DataSetBindings.Where(b => ownerIds.Contains(b.OwnerId)).ToListAsync(ct);

        var touchedBindings = 0;
        foreach (var b in bindings)
        {
            var key = b.TargetFieldKey == oldKey ? newKey : b.TargetFieldKey;

            // Ключи маппинга трогаем ТОЛЬКО у скалярной привязки. У табличной они принадлежат типу
            // СТРОКИ, а не владельцу (резолвер ищет их в rowFields, редактор предлагает поля типа
            // элемента), и переименование поля документа к ним отношения не имеет. Одноимённое поле
            // в типе строки — не редкость («Номер» и там, и там), и слепой перенос сломал бы
            // работающий маппинг, оставив в строке ключ, которого в её типе нет. Ту же границу
            // соблюдает BindingKeyAuditor — иначе аудит и перенос разошлись бы в понимании того,
            // чьё это поле.
            var mapping = b.Mapping;
            var mappingChanged = false;
            if (b.TargetFieldKey is null)
                mapping = RenameMappingKey(b.Mapping, oldKey, newKey, out mappingChanged);

            if (key == b.TargetFieldKey && !mappingChanged) continue;
            b.Update(key, mapping);
            touchedBindings++;
        }

        var templates = documentTypeIds.Count == 0
            ? []
            : await db.DataSetBindingTemplates.Where(t => documentTypeIds.Contains(t.DocumentTypeId)).ToListAsync(ct);

        var touchedTemplates = 0;
        foreach (var t in templates)
        {
            var key = t.TargetFieldKey == oldKey ? newKey : t.TargetFieldKey;
            // Та же граница, что и у привязки: у табличного шаблона ключи ColumnMappings — поля
            // типа строки.
            var mappings = t.ColumnMappings;
            var mappingChanged = false;
            if (t.TargetFieldKey is null)
                mappings = RenameMappingKey(t.ColumnMappings, oldKey, newKey, out mappingChanged);

            if (key == t.TargetFieldKey && !mappingChanged) continue;
            t.Update(t.Name, key, mappings, t.SortOrder);
            touchedTemplates++;
        }

        if (touchedBindings + touchedTemplates > 0) await db.SaveChangesAsync(ct);
        return new BindingKeyMigrationResult(touchedBindings, touchedTemplates);
    }

    /// <summary>
    /// Переименование КЛЮЧА в JSON-маппинге «поле → колонка». Значение (имя колонки в файле) не
    /// трогаем: переименовали поле схемы, а не заголовок в источнике.
    ///
    /// <para>В занятую цель не пишем — то же правило, что у миграции данных
    /// (<c>JsonPathEditor.Rename</c>): если новый ключ в маппинге уже есть, значит привязку успели
    /// перенастроить вручную, и её настройка авторитетнее нашей догадки.</para>
    /// </summary>
    private static string RenameMappingKey(string mappingJson, string oldKey, string newKey, out bool changed)
    {
        changed = false;
        Dictionary<string, string>? map;
        try
        {
            map = JsonSerializer.Deserialize<Dictionary<string, string>>(mappingJson);
        }
        catch (JsonException)
        {
            // Неразбираемый маппинг оставляем как есть. Уронить здесь значило бы оборвать перенос
            // на полпути: реквизиты уже переехали на новый ключ, а привязки остались на старом —
            // ровно то расхождение, ради устранения которого перенос и написан.
            return mappingJson;
        }
        if (map is null) return mappingJson;
        if (!map.TryGetValue(oldKey, out var value) || map.ContainsKey(newKey)) return mappingJson;

        map.Remove(oldKey);
        map[newKey] = value;
        changed = true;
        return JsonSerializer.Serialize(map);
    }

    public async Task<DataSetBindingDto?> CreateBindingAsync(CreateBindingInput input, CancellationToken ct)
    {
        var source = await db.DataSetSources.Include(s => s.File)
            .FirstOrDefaultAsync(s => s.Id == input.SourceId, ct);
        if (source == null) return null;

        var binding = DataSetBinding.For(input.OwnerId, input.SourceId, input.TargetFieldKey,
            DataSetDtoMapper.SerializeMapping(input.Mapping));
        db.DataSetBindings.Add(binding);
        await db.SaveChangesAsync(ct);

        await db.Entry(binding).Reference(b => b.Source).LoadAsync(ct);
        await db.Entry(binding.Source).Reference(s => s.File).LoadAsync(ct);
        return DataSetDtoMapper.MapBinding(binding);
    }

    public async Task<DataSetBindingDto?> UpdateBindingAsync(Guid id, UpdateBindingInput input, CancellationToken ct)
    {
        var binding = await db.DataSetBindings.Include(b => b.Source).ThenInclude(s => s.File)
            .FirstOrDefaultAsync(b => b.Id == id, ct);
        if (binding == null) return null;

        binding.Update(input.TargetFieldKey, DataSetDtoMapper.SerializeMapping(input.Mapping));
        await db.SaveChangesAsync(ct);
        return DataSetDtoMapper.MapBinding(binding);
    }

    public async Task<bool> DeleteBindingAsync(Guid id, CancellationToken ct)
    {
        var binding = await db.DataSetBindings.FindAsync([id], ct);
        if (binding == null) return false;
        db.DataSetBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<BindingPreviewDto>> PreviewBindingsAsync(Guid ownerId, CancellationToken ct)
    {
        var bindings = await db.DataSetBindings
            .Include(b => b.Source).ThenInclude(s => s.File)
            .Where(b => b.OwnerId == ownerId)
            .AsNoTracking()
            .ToListAsync(ct);

        // Владелец нужен дважды: чтобы знать комплект, в котором будут разворачиваться ссылки строк
        // (в другом комплекте резолвер документ не возьмёт), и чтобы вывести тип строки табличной
        // привязки. Читаем один раз на весь предпросмотр, а не по привязке.
        var owner = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)
            .FirstOrDefaultAsync(o => o.Id == ownerId, ct);
        var ownerSetId = owner is { IsDocument: true, ScopeLevel: CatalogScope.Set } ? owner.ScopeId : null;

        var results = new List<BindingPreviewDto>();
        foreach (var binding in bindings)
        {
            try
            {
                var rows = await rowLoader.LoadRowsAsync(binding.Source, ct);

                // Материализация ссылкой на существующий документ (issue #725) — до маппинга, его в
                // этом режиме нет. Показываем НАИМЕНОВАНИЯ документов: идентификатор в таблице не
                // говорит человеку ничего, а именно сюда он идёт проверять, те ли документы уедут.
                if (binding.Source.MaterializeTypeId is not null
                    && DataSetMappingValue.IsEmptyMapping(binding.Mapping)
                    && MaterializeByIdMode.IsOn(binding.Source.MaterializeByIdColumn))
                {
                    results.Add(await PreviewDocumentRefsAsync(binding, rows, ownerSetId, ct));
                    continue;
                }

                // Материализованный источник (issue #19/#23): привязка без своего маппинга берёт маппинг
                // с источника — как и резолвер генерации. Иначе превью пустое и материалы/сертификаты не
                // извлекаются на вкладке «Документы качества».
                var mappingJson = DataSetMappingValue.EffectiveMappingJson(
                    binding.Mapping, binding.Source.MaterializeTypeId, binding.Source.MaterializeMapping);
                var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(mappingJson) ?? [];

                // Вариант union'а по типу документа строки (issue #716) — тот же выбор, что делает
                // генерация. Показывать здесь ВСЕ варианты сразу значило бы врать о результате: в
                // документ уедет ровно один ключ на строку, а строки без варианта не уедут вовсе.
                // Экран этот открывают, чтобы понять, почему поле выглядит не так, — и он обязан
                // показывать то же, что получится.
                var selector = binding.Source.MaterializeTypeId is not null
                    && DataSetMappingValue.IsEmptyMapping(binding.Mapping)
                    ? await BuildVariantSelectorAsync(binding.Source.MaterializeDiscriminator, rows, ct)
                    : null;

                // Материализация настроена, а маппинг пуст (issue #715) — то же, о чём говорит
                // резолвер при генерации. Сказать это ОБЯЗАН и предпросмотр: именно сюда человек
                // идёт выяснять, почему поле пустое, и таблица пустых объектов ответа не даёт.
                if (binding.Source.MaterializeTypeId is not null && DataSetMappingValue.IsEmptyMapping(mappingJson))
                {
                    results.Add(new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
                        "error", binding.TargetFieldKey, rows.Count, new { },
                        $"Источник «{binding.Source.Name}» материализован, но маппинг колонок пуст — " +
                        "поле не заполняется. Задайте маппинг в диалоге материализации источника."));
                    continue;
                }

                var skipped = 0;
                if (binding.TargetFieldKey is null)
                {
                    var row = rows.Count > 0 ? rows[0] : null;
                    var data = new Dictionary<string, object?>();
                    foreach (var (fieldKey, colName) in PairsFor(selector, mapping, row, ref skipped))
                        if (!string.IsNullOrEmpty(colName))
                            data[fieldKey] = await DataSetDtoMapper.PreviewCellAsync(colName, row, ct);

                    // Тип строки скалярной привязки — сам тип владельца: маппинг ложится на его поля.
                    await DocRefPreviewLabeler.LabelAsync(db, [data], owner?.CompositeTypeId, ownerSetId, ct);

                    results.Add(new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
                        "scalar", null, rows.Count, data, null));
                }
                else
                {
                    var mapped = new List<Dictionary<string, object?>>();
                    foreach (var row in rows)
                    {
                        var pairs = PairsFor(selector, mapping, row, ref skipped);
                        if (selector is not null && pairs.Count == 0) continue;

                        var obj = new Dictionary<string, object?>();
                        foreach (var (fieldKey, colName) in pairs)
                            if (!string.IsNullOrEmpty(colName))
                                obj[fieldKey] = await DataSetDtoMapper.PreviewCellAsync(colName, row, ct);
                        mapped.Add(obj);
                    }

                    // Тип строки: у материализованного источника — его тип, иначе — тип элемента
                    // целевого поля. Без этого doc-ref-ячейки остались бы сырыми идентификаторами
                    // на экране, куда идут проверять, ТЕ ЛИ документы приедут.
                    await DocRefPreviewLabeler.LabelAsync(db, mapped,
                        await RowTypeIdAsync(binding, owner?.CompositeTypeId, ct), ownerSetId, ct);

                    results.Add(new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
                        "tabular", binding.TargetFieldKey, mapped.Count, mapped,
                        // Пропущенные не прячем в ноль строк: «показано меньше, чем в источнике» —
                        // это то, ради чего сюда и смотрят.
                        skipped > 0
                            ? $"Строк пропущено при материализации: {skipped} — их тип документа не назначен ни одному варианту."
                            : null));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Не удалось построить предпросмотр привязки {BindingId}", binding.Id);
                results.Add(new BindingPreviewDto(binding.Id, binding.Source?.Name ?? "?",
                    binding.Source?.File?.Name ?? "?", "error", binding.TargetFieldKey, 0, new { }, ex.Message));
            }
        }
        return results;
    }

    /// <summary>
    /// Предпросмотр материализации ссылкой на существующий документ (issue #725): по строке на
    /// документ, значение — наименование, а для отсутствующего документа сказано, что его нет.
    /// Пропущенные строки (пустая ячейка, не-Ид) показываются числом с причиной — как и у генерации.
    /// </summary>
    private async Task<BindingPreviewDto> PreviewDocumentRefsAsync(
        DataSetBinding binding,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        Guid? ownerSetId,
        CancellationToken ct)
    {
        var column = binding.Source.MaterializeByIdColumn!;

        if (binding.TargetFieldKey is null)
            return new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
                "error", null, rows.Count, new { },
                $"Источник «{binding.Source.Name}» материализован ссылкой на существующий документ — " +
                "такую строку можно положить только в поле-документ или в список документов, а не в отдельные поля.");

        var ids = new List<Guid>();
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var (id, reason, _) = MaterializeByIdMode.ReadId(row, column);
            if (id is null) skipped[reason!] = skipped.GetValueOrDefault(reason!) + 1;
            else ids.Add(id.Value);
        }

        var labels = await MaterializeByIdMode.ResolveLabelsAsync(db, ids.Distinct().ToList(), ownerSetId, ct);
        var mapped = ids
            .Select(id => new Dictionary<string, object?>
            {
                ["Документ"] = labels.TryGetValue(id, out var label) ? label : MaterializeByIdMode.NotFoundLabel,
            })
            .ToList();

        var warning = skipped.Count == 0
            ? null
            : "Строк пропущено при материализации: " + skipped.Values.Sum() + " — "
              + string.Join("; ", skipped.OrderByDescending(p => p.Value)
                  .Select(p => $"{p.Value} — {MaterializeSkipReason.Describe(p.Key)}"));

        return new BindingPreviewDto(binding.Id, binding.Source.Name, binding.Source.File.Name,
            "tabular", binding.TargetFieldKey, mapped.Count, mapped, warning);
    }

    /// <summary>
    /// Тип, в форму которого разворачивается строка табличной привязки: у материализованного
    /// источника — его тип материализации (маппинг взят оттуда), иначе — тип элемента целевого поля.
    /// null — вывести не удалось (тип владельца неизвестен либо поле не найдено).
    /// </summary>
    private async Task<Guid?> RowTypeIdAsync(DataSetBinding binding, Guid? ownerTypeId, CancellationToken ct)
    {
        if (binding.Source.MaterializeTypeId is { } materialized
            && DataSetMappingValue.IsEmptyMapping(binding.Mapping))
            return materialized;

        if (ownerTypeId is not { } typeId || binding.TargetFieldKey is null) return null;

        var typesById = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        return DocumentTypeSchemaReader.Field(typeId, binding.TargetFieldKey, typesById)?.TypeId;
    }

    /// <summary>Тот же выбор варианта, что у генерации (issue #716); null — правила нет.</summary>
    private async Task<MaterializeVariantSelector?> BuildVariantSelectorAsync(
        string? discriminatorJson,
        IReadOnlyList<IReadOnlyDictionary<string, string?>> rows,
        CancellationToken ct)
    {
        var config = MaterializeVariantSelector.ParseConfig(discriminatorJson);
        if (config is null) return null;

        var typesById = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        Dictionary<Guid, Guid>? typeByDocument = null;
        var documentIds = MaterializeVariantSelector.DocumentIdsIn(config, rows).ToList();
        if (documentIds.Count > 0)
            typeByDocument = await db.DomainObjects.AsNoTracking()
                .Where(o => documentIds.Contains(o.Id))
                .ToDictionaryAsync(o => o.Id, o => o.CompositeTypeId, ct);

        return MaterializeVariantSelector.Create(config, typesById, typeByDocument);
    }

    /// <summary>Что показать по этой строке: без правила — весь маппинг, с правилом — пару
    /// победившего варианта. Пустой список = строка пропущена.</summary>
    private static List<KeyValuePair<string, string>> PairsFor(
        MaterializeVariantSelector? selector,
        Dictionary<string, string> mapping,
        IReadOnlyDictionary<string, string?>? row,
        ref int skipped)
    {
        if (selector is null || row is null) return [.. mapping];

        var choice = selector.Choose(row);
        if (choice.VariantKey is null || !mapping.TryGetValue(choice.VariantKey, out var token))
        {
            skipped++;
            return [];
        }
        return [new KeyValuePair<string, string>(choice.VariantKey, token)];
    }
}
