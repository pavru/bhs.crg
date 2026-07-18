using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Resolution;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Generation;

public class DataSetResolver(
    AppDbContext db,
    IBlobStorage blobStorage,
    DataSetParserFactory parserFactory,
    IObjectResolver objectResolver,
    ILogger<DataSetResolver> logger
) : IDataSetResolver
{
    /// <summary>Генерация документа: резолвит привязки владельца в контекст (scope — из комплекта документа).</summary>
    public Task InjectAsync(GenerationContext ctx, DocumentView instance,
        List<ResolutionDiagnostic>? diagnostics = null, CancellationToken ct = default)
        => ResolveBindingsCoreAsync(ctx, instance.Id, instance.DocumentTypeId,
            CatalogScope.Set, instance.DocumentSetId, diagnostics, ct);

    /// <summary>
    /// Резолв привязок для ПЕРСИСТА (issue #99): sync-on-save общих данных. Прогоняет тот же резолв-путь,
    /// что и генерация (@@ref → {$ref:catalog, entryId}, нет матча → пропуск + WARNING), но scope берётся
    /// из расположения объекта (ScopeLevel, ScopeId), а результат отдаётся значениями для слияния в Data.
    /// Ключевое отличие от превью: здесь резолвится ЗНАЧЕНИЕ (ссылка), а не display-строка «🔗 …».
    /// </summary>
    public async Task<IReadOnlyDictionary<string, object?>> ResolveOwnerBindingsAsync(
        Guid ownerId, Guid typeId, CatalogScope scopeLevel, Guid? scopeId,
        List<ResolutionDiagnostic>? diagnostics = null, CancellationToken ct = default)
    {
        var ctx = new GenerationContext();
        await ResolveBindingsCoreAsync(ctx, ownerId, typeId, scopeLevel, scopeId, diagnostics, ct);
        return ctx.Data;
    }

    private async Task ResolveBindingsCoreAsync(GenerationContext ctx, Guid ownerId, Guid typeId,
        CatalogScope scopeLevel, Guid? scopeId, List<ResolutionDiagnostic>? diagnostics, CancellationToken ct)
    {
        var bindings = await db.DataSetBindings
            .Include(b => b.Source).ThenInclude(s => s.File)
            .Where(b => b.OwnerId == ownerId)
            .AsNoTracking()
            .ToListAsync(ct);

        if (bindings.Count == 0) return;

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
                            var value = await MapValueAsync(mapVal, row, ownerId, scopeLevel, scopeId, diagnostics, fieldKey, ct);
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
                    var field = DocumentTypeSchemaReader.Field(typeId, binding.TargetFieldKey, await TypesAsync());

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
                            var value = await MapValueAsync(mapVal, row, ownerId, scopeLevel, scopeId, diagnostics, path, ct);
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
                    "Привязка набора данных пропущена. BindingId={BindingId}, SourceId={SourceId}, Owner={OwnerId}",
                    binding.Id, binding.SourceId, ownerId);
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
    /// Поиск существующего объекта делегируется единому <see cref="IObjectResolver"/> (issue #183):
    /// матч по конкретному полю (@@ref с match) или по имени/алиасам (пустой match). Нет матча →
    /// WARNING + поле не добавляется (создание объектов не выполняется — резолвер read-only).
    /// </summary>
    private async Task<object?> MapValueAsync(
        string mapVal,
        IReadOnlyDictionary<string, string?> row,
        Guid ownerId,
        CatalogScope scopeLevel,
        Guid? scopeId,
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

        // Ссылочное поле: резолвим строку в существующий объект каталога по одной из двух стратегий
        // (issue #243): Identity (составной ключ identity-полей) либо Name (по имени/алиасам); legacy
        // с непустым match — Field (произвольное поле, из UI больше не создаётся, читается вечно).
        ObjectMatchRequest req;
        string lookupDisplay; // для WARNING
        if (refMap.IsIdentity)
        {
            var fields = new Dictionary<string, string?>();
            foreach (var (idField, col) in refMap.IdentityColumns!)
                fields[idField] = row.TryGetValue(col, out var cv) ? cv : null;
            if (fields.Values.All(string.IsNullOrWhiteSpace)) return null; // нечего искать
            req = ObjectMatchRequest.ByIdentity(refMap.TypeId, fields);
            lookupDisplay = string.Join(" · ", fields.Values.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
        else
        {
            if (refMap.Column is null || !row.TryGetValue(refMap.Column, out var lookup) || string.IsNullOrWhiteSpace(lookup))
                return null;
            req = string.IsNullOrEmpty(refMap.Match)
                ? ObjectMatchRequest.ByName(refMap.TypeId, lookup)
                : ObjectMatchRequest.ByField(refMap.TypeId, refMap.Match, lookup);
            lookupDisplay = lookup;
        }

        var entryId = await objectResolver.ResolveAsync(req, scopeLevel, scopeId, ct);
        if (entryId is null)
        {
            logger.LogWarning(
                "Запись каталога не найдена при маппинге набора данных. TypeId={TypeId}, Strategy={Strategy}, Value={Value}, Owner={OwnerId}",
                refMap.TypeId, req.Strategy, lookupDisplay, ownerId);
            diagnostics?.Add(new ResolutionDiagnostic(
                DiagnosticSeverity.Warning, path,
                $"Значение «{lookupDisplay}» не найдено в каталоге — ссылка не подставлена."));
            return null;
        }

        return new Dictionary<string, object?> { ["$ref"] = "catalog", ["entryId"] = entryId.Value.ToString() };
    }

}
