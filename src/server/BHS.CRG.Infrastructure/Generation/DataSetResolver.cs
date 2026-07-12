using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Common;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Generation;

public class DataSetResolver(
    AppDbContext db,
    IBlobStorage blobStorage,
    DataSetParserFactory parserFactory,
    ILogger<DataSetResolver> logger
) : IDataSetResolver
{
    public async Task InjectAsync(GenerationContext ctx, DocumentInstance instance,
        List<ResolutionDiagnostic>? diagnostics = null, CancellationToken ct = default)
    {
        var bindings = await db.DataSetBindings
            .Include(b => b.Source).ThenInclude(s => s.File)
            .Where(b => b.InstanceId == instance.Id)
            .AsNoTracking()
            .ToListAsync(ct);

        if (bindings.Count == 0) return;

        // Цепочка scope каталога (Set → Section → Construction → System) и кэш записей
        // по составному типу — строятся лениво, только если встретится ссылочный маппинг.
        Task<ScopeChain>? scopeChainTask = null;
        Task<ScopeChain> ChainAsync() => scopeChainTask ??= ScopeChains.LoadAsync(db, instance.DocumentSetId, ct);
        var entryCache = new Dictionary<Guid, List<CommonDataEntry>>();

        // Схема типов (для кардинальности целевого поля материализации/табличной связки) — лениво, один раз.
        Dictionary<Guid, DocumentType>? typesById = null;
        async Task<Dictionary<Guid, DocumentType>> TypesAsync() =>
            typesById ??= await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);

        foreach (var binding in bindings)
        {
            try
            {
                // Download → parse → transformation → filter → sort (shared with preview via DataSetBindingProcessor).
                var rows = await DataSetBindingProcessor.LoadRowsAsync(blobStorage, parserFactory, binding.Source, ct);

                // Материализация на источнике (issue #19): если источник настроен на материализацию, а
                // привязка не несёт собственного маппинга — маппинг берётся с источника (тип↔тип), а
                // привязка играет роль типизированного указателя. Иначе — легаси-маппинг привязки.
                var mappingJson = DataSetMappingValue.EffectiveMappingJson(
                    binding.Mapping, binding.Source.MaterializeTypeId, binding.Source.MaterializeMapping);
                var mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(mappingJson) ?? [];

                if (binding.TargetFieldKey is null)
                {
                    // Скалярный: первая строка → отдельные поля контекста
                    if (rows.Count > 0)
                    {
                        var row = rows[0];
                        foreach (var (fieldKey, mapVal) in mapping)
                        {
                            var value = await MapValueAsync(mapVal, row, instance, ChainAsync, entryCache, diagnostics, fieldKey, ct);
                            if (value is not null)
                                ctx.Set(fieldKey, value);
                        }
                    }
                }
                else
                {
                    // Кардинальность решает ТИП целевого поля: complex/doc-ref ← первая сущность;
                    // array/doc-array (и всё прочее) ← весь поток. Вычисляем ДО построения строк —
                    // нужен и для кардинальности, и для defaultValue (issue #53, часть 2).
                    var field = DocumentTypeSchemaReader.Field(instance.DocumentTypeId, binding.TargetFieldKey, await TypesAsync());

                    // defaultValue незамапленных полей ТИПА СТРОКИ (issue #53, часть 2): для табличных
                    // биндингов маппинг покрывает только явно перечисленные ключи (свои — binding.Mapping,
                    // либо MaterializeMapping источника) — поля целевого типа, не попавшие в маппинг (но
                    // имеющие defaultValue схемы, напр. через fieldOverrides унаследованного поля), иначе
                    // никогда не появляются в результате. Тип строки — MaterializeTypeId источника, если
                    // маппинг взят оттуда (см. EffectiveMappingJson), иначе — типId самого целевого поля.
                    var usingMaterializeMapping = binding.Source.MaterializeTypeId is not null
                        && DataSetMappingValue.IsEmptyMapping(binding.Mapping);
                    var rowTypeId = usingMaterializeMapping ? binding.Source.MaterializeTypeId : field?.TypeId;
                    var rowDefaults = rowTypeId is { } rtid
                        ? DocumentTypeSchemaReader.EffectiveFields(rtid, await TypesAsync())
                            .Where(f => f.DefaultValue is not null && SchemaFieldKinds.IsScalar(f.Type))
                            .ToList()
                        : [];

                    // Все строки → объекты формы целевого типа. Храним как JsonElement, чтобы повторный
                    // проход EntityResolver разрешил добавленные ссылки $ref на каталог.
                    var mapped = new List<Dictionary<string, object?>>();
                    var rowIndex = 0;
                    foreach (var row in rows)
                    {
                        var obj = new Dictionary<string, object?>();
                        foreach (var (fieldKey, mapVal) in mapping)
                        {
                            var path = $"{binding.TargetFieldKey}[{rowIndex}].{fieldKey}";
                            var value = await MapValueAsync(mapVal, row, instance, ChainAsync, entryCache, diagnostics, path, ct);
                            if (value is not null)
                                obj[fieldKey] = value;
                        }
                        // Приоритет ниже маппинга: значение из строки (уже в obj) > defaultValue схемы.
                        foreach (var f in rowDefaults)
                            if (!obj.ContainsKey(f.Key))
                                obj[f.Key] = f.DefaultValue!.Value;
                        mapped.Add(obj);
                        rowIndex++;
                    }

                    if (field is not null && DocumentTypeSchemaReader.IsSingleComposite(field.Type))
                    {
                        if (mapped.Count > 0)
                            ctx.Set(binding.TargetFieldKey, JsonSerializer.SerializeToElement(mapped[0]));
                    }
                    else
                    {
                        ctx.Set(binding.TargetFieldKey, JsonSerializer.SerializeToElement(mapped));
                    }
                }
            }
            catch (Exception ex)
            {
                // Пропускаем невалидные привязки, чтобы не блокировать генерацию,
                // но фиксируем причину — иначе "пустые" поля невозможно отладить.
                logger.LogWarning(ex,
                    "Привязка набора данных пропущена при генерации. BindingId={BindingId}, SourceId={SourceId}, Instance={InstanceId}",
                    binding.Id, binding.SourceId, instance.Id);
                // Иначе поле просто исчезает без следа — поднимаем причину в диагностику.
                diagnostics?.Add(new ResolutionDiagnostic(
                    DiagnosticSeverity.Error,
                    binding.TargetFieldKey ?? "(скалярная привязка)",
                    $"Источник данных недоступен — поле не заполнено. {ex.Message}"));
            }
        }
    }

    /// <summary>
    /// Преобразует одно значение маппинга: обычная колонка → строка;
    /// ссылочный маппинг (@@ref) → объект {$ref:catalog, entryId} по найденной записи каталога.
    /// Возвращает null, если значение отсутствует/не найдено (поле не добавляется).
    /// </summary>
    private async Task<object?> MapValueAsync(
        string mapVal,
        IReadOnlyDictionary<string, string?> row,
        DocumentInstance instance,
        Func<Task<ScopeChain>> scopeChainAccessor,
        Dictionary<Guid, List<CommonDataEntry>> entryCache,
        List<ResolutionDiagnostic>? diagnostics,
        string path,
        CancellationToken ct)
    {
        var fileMap = DataSetMappingValue.ParseFile(mapVal);
        if (fileMap is not null)
            return DataSetMappingValue.ResolveFileValue(fileMap, row);

        var refMap = DataSetMappingValue.ParseRef(mapVal);
        if (refMap is null)
            return row.TryGetValue(mapVal, out var val) ? val : null;

        // Ссылочное поле: ищем запись каталога по значению колонки.
        if (!row.TryGetValue(refMap.Column, out var lookup) || string.IsNullOrWhiteSpace(lookup))
            return null;

        var chain = await scopeChainAccessor();
        var entryId = await FindCatalogEntryIdAsync(refMap.TypeId, refMap.Match, lookup, chain, entryCache, ct);
        if (entryId is null)
        {
            logger.LogWarning(
                "Запись каталога не найдена при маппинге набора данных. TypeId={TypeId}, Match={Match}, Value={Value}, Instance={InstanceId}",
                refMap.TypeId, refMap.Match, lookup, instance.Id);
            diagnostics?.Add(new ResolutionDiagnostic(
                DiagnosticSeverity.Warning, path,
                $"Значение «{lookup}» не найдено в каталоге — ссылка не подставлена."));
            return null;
        }

        return new Dictionary<string, object?> { ["$ref"] = "catalog", ["entryId"] = entryId.Value.ToString() };
    }

    private async Task<Guid?> FindCatalogEntryIdAsync(
        Guid typeId, string match, string value, ScopeChain chain,
        Dictionary<Guid, List<CommonDataEntry>> entryCache, CancellationToken ct)
    {
        if (!entryCache.TryGetValue(typeId, out var candidates))
        {
            candidates = await db.CommonDataEntries
                .AsNoTracking()
                .Where(e => e.CompositeTypeId == typeId &&
                    ((e.Scope == CatalogScope.Set && e.ScopeId == chain.SetId) ||
                     (e.Scope == CatalogScope.Section && e.ScopeId == chain.SectionId) ||
                     (e.Scope == CatalogScope.Construction && e.ScopeId == chain.ConstructionId) ||
                     e.Scope == CatalogScope.System))
                .ToListAsync(ct);
            // Приоритет: Set=1 (высший) … System=5 (низший).
            candidates = candidates.OrderBy(e => (int)e.Scope).ToList();
            entryCache[typeId] = candidates;
        }

        var needle = Normalize(value);
        if (needle.Length == 0) return null; // пустое значение ни с чем не сопоставляем
        foreach (var entry in candidates)
        {
            var hay = string.IsNullOrEmpty(match)
                ? entry.DisplayName
                : ReadDataField(entry.Data, match);
            if (hay is not null && Normalize(hay) == needle)
                return entry.Id;
        }
        return null;
    }

    // Нормализация для сопоставления: регистр, окружающие пробелы и завершающие
    // точки/пробелы игнорируются — "шт.", "Шт", "шт " считаются равными "шт".
    private static string Normalize(string s) => s.Trim().TrimEnd('.', ' ').ToLowerInvariant();

    private static string? ReadDataField(JsonDocument data, string field)
    {
        if (!data.RootElement.TryGetProperty(field, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

}
