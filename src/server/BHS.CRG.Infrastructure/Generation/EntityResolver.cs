using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Common;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// C#-аналог NewElementResolverStyles.xsl: разрешает $ref-объекты в реквизитах,
/// подмешивает данные сущностей каталога в контекст генерации.
/// </summary>
public class EntityResolver(AppDbContext db) : IEntityResolver
{
    // Максимальная глубина рекурсивного разворачивания вложенных ссылок (защита от патологически
    // глубоких структур). Ортогональна allowInstanceRefs (защита от циклов по документам).
    private const int MaxRefDepth = 8;

    public async Task<GenerationContext> ResolveAsync(DocumentView instance, CancellationToken ct = default)
    {
        // Наследование от базового экземпляра (issue #71): если реквизиты несут "_baseRef", подмешиваем
        // реквизиты базы — документа комплекта ЛИБО записи общих данных (полиморфно, по скоп-близости) —
        // собственные поля переопределяют. Только Requisites; PluginData (per-instance кэш плагинов)
        // не наследуется. Резолв-тайм — ничего не персистим. visited стартует с текущего инстанса.
        GenerationContext ctx;
        var reqRoot = instance.Requisites.RootElement;
        if (reqRoot.ValueKind == JsonValueKind.Object && reqRoot.TryGetProperty("_baseRef", out _))
        {
            var scope = await ScopeChains.LoadAsync(db, instance.DocumentSetId, ct);
            var effReq = await ResolveObjectBaseRefAsync(reqRoot, scope, [instance.Id], ct);
            using var effReqDoc = JsonSerializer.SerializeToDocument(effReq);
            ctx = GenerationContext.FromJson(effReqDoc, instance.PluginData);
        }
        else
        {
            ctx = GenerationContext.FromJson(instance.Requisites, instance.PluginData);
        }

        await ResolveContextRefsAsync(ctx, instance.DocumentSetId, ct);
        return ctx;
    }

    public async Task ResolveContextRefsAsync(GenerationContext ctx, Guid documentSetId, CancellationToken ct = default)
    {
        foreach (var key in ctx.Data.Keys.ToList())
        {
            if (ctx.Data[key] is not JsonElement el) continue;
            ctx.Set(key, await ResolveNode(el, documentSetId, depth: 0, allowInstanceRefs: true, ct));
        }
    }

    public async Task ApplyDefaultsAsync(GenerationContext ctx, DocumentView instance, CancellationToken ct = default)
    {
        var allDocTypes = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var fields = DocumentTypeSchemaReader.EffectiveFields(instance.DocumentTypeId, allDocTypes);
        foreach (var f in fields)
        {
            if (f.DefaultValue is null) continue;
            if (!SchemaFieldKinds.IsScalar(f.Type)) continue; // complex/array/doc-ref/doc-array/file/image — не трогаем
            if (ctx.Data.ContainsKey(f.Key)) continue; // уже задано инстансом или биндингом — не перезаписываем
            ctx.Set(f.Key, f.DefaultValue.Value);
        }
    }

    /// <summary>
    /// Резолвит enum-поля реквизитов из кода в отображаемое имя перед генерацией (issue #59): в
    /// реквизитах хранится стабильный код EnumType.Values, но в PDF должен попасть человекочитаемый
    /// текст. Scope сознательно ограничен верхнеуровневыми скалярными полями — то же ограничение,
    /// что уже есть у ApplyDefaultsAsync (не резолвит внутрь строк array/complex полей). Толерантно:
    /// код без совпадения в Options остаётся как есть (та же философия, что и везде в резолвере).
    /// </summary>
    public async Task ResolveEnumLabelsAsync(GenerationContext ctx, DocumentView instance, CancellationToken ct = default)
    {
        var allDocTypes = await db.DocumentTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, ct);
        var enumTypesById = await db.EnumTypes.AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var fields = DocumentTypeSchemaReader.EffectiveFields(instance.DocumentTypeId, allDocTypes, enumTypesById);
        foreach (var f in fields)
        {
            if (f.Type != "enum" || f.Options is null || f.Options.Count == 0) continue;
            if (!ctx.Data.TryGetValue(f.Key, out var raw)) continue;
            // ctx.Data хранит JsonElement (реквизиты из FromJson) — но допускаем и обычную строку
            // (напр. если значение положено напрямую, не через JSON-парсинг).
            var code = raw switch
            {
                JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
                string s => s,
                _ => null,
            };
            if (code is null) continue;
            var match = f.Options.FirstOrDefault(o => o.Code == code);
            if (match is not null) ctx.Set(f.Key, match.Label);
        }
    }

    /// <summary>
    /// Единая точка разбора $ref: обходит произвольное JSON-дерево (объекты/массивы на любой
    /// глубине — раньше это были три разошедшиеся копии). <paramref name="depth"/> — общий предел
    /// глубины графа; <paramref name="allowInstanceRefs"/> — разрешено ли на этом шаге разворачивать
    /// instance-ссылки (становится false внутри уже развёрнутого instance — один переход по
    /// документу, защита от циклов A→B→C).
    /// </summary>
    private async Task<JsonElement> ResolveNode(JsonElement node, Guid documentSetId, int depth, bool allowInstanceRefs, CancellationToken ct)
    {
        if (depth >= MaxRefDepth) return node.Clone();

        switch (node.ValueKind)
        {
            case JsonValueKind.Object when node.TryGetProperty("$ref", out var refTypeProp):
                return await ResolveRefObject(node, refTypeProp.GetString(), documentSetId, depth, allowInstanceRefs, ct);

            case JsonValueKind.Object:
            {
                var dict = new Dictionary<string, JsonElement>();
                foreach (var prop in node.EnumerateObject())
                    dict[prop.Name] = await ResolveNode(prop.Value, documentSetId, depth + 1, allowInstanceRefs, ct);
                return JsonSerializer.SerializeToElement(dict);
            }

            case JsonValueKind.Array:
            {
                var list = new List<JsonElement>();
                foreach (var item in node.EnumerateArray())
                    list.Add(await ResolveNode(item, documentSetId, depth + 1, allowInstanceRefs, ct));
                return JsonSerializer.SerializeToElement(list);
            }

            default:
                return node.Clone();
        }
    }

    private async Task<JsonElement> ResolveRefObject(JsonElement node, string? refType, Guid documentSetId, int depth, bool allowInstanceRefs, CancellationToken ct)
    {
        switch (refType)
        {
            // Запись каталога (с _baseRef-наследованием) → заходим внутрь, её собственные ссылки тоже разворачиваем.
            case "catalog"
                when node.TryGetProperty("entryId", out var entryIdProp) && Guid.TryParse(entryIdProp.GetString(), out var entryId):
            {
                var resolved = await ResolveEntryByIdAsync(entryId, [], ct);
                return resolved.ValueKind == JsonValueKind.Undefined
                    ? node.Clone()
                    : await ResolveNode(resolved, documentSetId, depth + 1, allowInstanceRefs, ct);
            }

            // Протягивание одного поля из реквизитов другого документа — значение как есть, без рекурсии
            // (сохраняем текущее поведение). Работает на любой глубине — это чинит баг A (ссылка в массиве).
            case "document"
                when node.TryGetProperty("instanceId", out var instIdProp) && Guid.TryParse(instIdProp.GetString(), out var instId)
                     && node.TryGetProperty("fieldKey", out var fieldKeyProp):
            {
                var fieldKey = fieldKeyProp.GetString() ?? string.Empty;
                var refObj = await db.DomainObjects.AsNoTracking()
                    .FirstOrDefaultAsync(o => o.Id == instId && o.ScopeLevel == CatalogScope.Set && o.ScopeId == documentSetId, ct);
                return refObj is not null && refObj.Data.RootElement.TryGetProperty(fieldKey, out var fieldVal)
                    ? fieldVal.Clone()
                    : node.Clone();
            }

            // Разворачивание другого документа — один раз (allowInstanceRefs → false внутри).
            case "instance"
                when allowInstanceRefs
                     && node.TryGetProperty("instanceId", out var docInstIdProp) && Guid.TryParse(docInstIdProp.GetString(), out var docInstId):
            {
                var resolved = await ResolveDocumentInstanceAsync(docInstId, documentSetId, depth, ct);
                return resolved.ValueKind != JsonValueKind.Undefined ? resolved : node.Clone();
            }

            // Неизвестный $ref или instance при allowInstanceRefs=false — оставляем как есть (клон).
            default:
                return node.Clone();
        }
    }

    /// <summary>
    /// Загружает DocumentInstance и разворачивает его поля той же единой обработкой, но с
    /// <c>allowInstanceRefs: false</c> — вложенные instance-ссылки дальше не разворачиваются
    /// (защита от циклов), а массивы/таблицы внутри обрабатываются как везде (это чинит баг B).
    /// </summary>
    private async Task<JsonElement> ResolveDocumentInstanceAsync(Guid instanceId, Guid documentSetId, int depth, CancellationToken ct)
    {
        var obj = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)
            .FirstOrDefaultAsync(o => o.Id == instanceId && o.ScopeLevel == CatalogScope.Set
                                      && o.ScopeId == documentSetId && o.Facet != null, ct);

        if (obj is null) return default;

        var subCtx = GenerationContext.FromJson(obj.Data, obj.Facet!.PluginData);
        var dict = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in subCtx.Data)
            if (v is JsonElement je)
                dict[k] = await ResolveNode(je, documentSetId, depth + 1, allowInstanceRefs: false, ct);

        return JsonSerializer.SerializeToElement(dict);
    }

    /// <summary>
    /// Рекурсивно разрешает объект общих данных по id с поддержкой _baseRef: если в data есть
    /// "_baseRef", подтягивает данные базового объекта и выполняет deep-merge (собственные поля
    /// имеют приоритет). Цепочка entry→entry со своим <paramref name="visited"/>-guard.
    /// </summary>
    private async Task<JsonElement> ResolveEntryByIdAsync(Guid id, HashSet<Guid> visited, CancellationToken ct)
    {
        if (!visited.Add(id))
            return default; // защита от циклических ссылок

        var obj = await db.DomainObjects.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (obj is null) return default;

        var ownData = obj.Data.RootElement;
        if (!ownData.TryGetProperty("_baseRef", out var baseRefEl) || ParseBaseRef(baseRefEl) is not { } baseId)
            return ownData;

        var baseData = await ResolveEntryByIdAsync(baseId, visited, ct);
        return baseData.ValueKind == JsonValueKind.Object ? MergeBaseObjects(baseData, ownData) : ownData;
    }

    /// <summary>
    /// Наследование от базового объекта (issue #71/#84): данные объекта могут нести "_baseRef" на
    /// ДРУГОЙ объект — документ комплекта ЛИБО запись общих данных (после слияния — единый DomainObject).
    /// Полиморфизм схлопнут: разновидность определяется природой цели (наличие фасеты), а не тегом ссылки.
    /// Guard: цель-документ — тот же комплект (same-set); цель-общие-данные — скоп-поддерево документа
    /// (иначе утечка чужих данных). Собственные поля переопределяют унаследованные (<see cref="MergeBaseObjects"/>).
    /// </summary>
    private async Task<JsonElement> ResolveObjectBaseRefAsync(
        JsonElement ownData, ScopeChain scope, HashSet<Guid> visited, CancellationToken ct)
    {
        if (ownData.ValueKind != JsonValueKind.Object) return ownData;
        if (!ownData.TryGetProperty("_baseRef", out var baseRefEl)) return ownData;
        if (ParseBaseRef(baseRefEl) is not { } id) return ownData;

        var baseObj = await db.DomainObjects.AsNoTracking().Include(o => o.Facet)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (baseObj is null) return ownData;

        JsonElement baseData;
        if (baseObj.IsDocument)
        {
            if (!visited.Add(id)) return ownData; // цикл/самоссылка → без наследования
            if (baseObj.ScopeLevel != CatalogScope.Set || baseObj.ScopeId != scope.SetId) return ownData; // same-set guard
            baseData = await ResolveObjectBaseRefAsync(baseObj.Data.RootElement, scope, visited, ct);
        }
        else // запись общих данных
        {
            if (!scope.Contains(baseObj.ScopeLevel, baseObj.ScopeId)) return ownData; // scope-subtree guard
            baseData = await ResolveEntryByIdAsync(id, visited, ct); // entry→entry цепочка + свой visited.Add(id)
        }

        return baseData.ValueKind == JsonValueKind.Object ? MergeBaseObjects(baseData, ownData) : ownData;
    }

    /// Разбор "_baseRef": дискриминированный объект {kind,id} (issue #71) или голый id-строка (legacy).
    /// После слияния объектов (issue #84) разновидность цели определяется её природой при резолве
    /// (наличие фасеты), поэтому здесь нужен только id — тег kind игнорируется.
    private static Guid? ParseBaseRef(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
            return Guid.TryParse(el.GetString(), out var g) ? g : null;
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("id", out var idEl) && Guid.TryParse(idEl.GetString(), out var gid))
            return gid;
        return null;
    }

    /// <summary>
    /// Слияние двух JSON-объектов для _baseRef-наследования: базовые поля первыми, собственные
    /// переопределяют их на верхнем уровне; ключ "_baseRef" исключается. Чистая функция —
    /// общая для наследования любых объектов (issue #71/#84).
    /// </summary>
    private static JsonElement MergeBaseObjects(JsonElement baseData, JsonElement ownData)
    {
        var merged = new Dictionary<string, JsonElement>();
        foreach (var p in baseData.EnumerateObject())
            if (p.Name != "_baseRef") merged[p.Name] = p.Value.Clone();
        foreach (var p in ownData.EnumerateObject())
            if (p.Name != "_baseRef") merged[p.Name] = p.Value.Clone();

        return JsonSerializer.SerializeToElement(merged);
    }
}
