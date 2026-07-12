using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
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

    public async Task<GenerationContext> ResolveAsync(DocumentInstance instance, CancellationToken ct = default)
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
            var effReq = await ResolveDocumentBaseRefAsync(reqRoot, scope, [instance.Id], ct);
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

    public async Task ApplyDefaultsAsync(GenerationContext ctx, DocumentInstance instance, CancellationToken ct = default)
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
    public async Task ResolveEnumLabelsAsync(GenerationContext ctx, DocumentInstance instance, CancellationToken ct = default)
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
                var resolved = await ResolveCommonDataEntryAsync(entryId, [], ct);
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
                var refInstance = await db.DocumentInstances.AsNoTracking()
                    .FirstOrDefaultAsync(i => i.Id == instId && i.DocumentSetId == documentSetId, ct);
                return refInstance is not null && refInstance.Requisites.RootElement.TryGetProperty(fieldKey, out var fieldVal)
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
        var instance = await db.DocumentInstances
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == instanceId && i.DocumentSetId == documentSetId, ct);

        if (instance is null) return default;

        var subCtx = GenerationContext.FromJson(instance.Requisites, instance.PluginData);
        var dict = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in subCtx.Data)
            if (v is JsonElement je)
                dict[k] = await ResolveNode(je, documentSetId, depth + 1, allowInstanceRefs: false, ct);

        return JsonSerializer.SerializeToElement(dict);
    }

    /// <summary>
    /// Рекурсивно разрешает CommonDataEntry с поддержкой _baseRef:
    /// если в data есть "_baseRef": "&lt;entryId&gt;", подтягивает данные базового экземпляра
    /// и выполняет deep-merge (собственные поля имеют приоритет над унаследованными).
    /// </summary>
    private async Task<JsonElement> ResolveCommonDataEntryAsync(Guid entryId, HashSet<Guid> visited, CancellationToken ct)
    {
        if (!visited.Add(entryId))
            return default; // защита от циклических ссылок

        var entry = await db.CommonDataEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == entryId, ct);

        if (entry is null) return default;

        var ownData = entry.Data.RootElement;

        if (!ownData.TryGetProperty("_baseRef", out var baseRefEl) ||
            !Guid.TryParse(baseRefEl.GetString(), out var baseEntryId))
            return ownData;

        var baseData = await ResolveCommonDataEntryAsync(baseEntryId, visited, ct);
        if (baseData.ValueKind != JsonValueKind.Object)
            return ownData;

        return MergeBaseObjects(baseData, ownData);
    }

    /// <summary>
    /// Полиморфное наследование от базового экземпляра для документов комплекта (issue #71): реквизиты
    /// документа могут нести "_baseRef" на ДРУГОЙ документ комплекта ЛИБО на запись общих данных.
    /// Формат — дискриминированный <c>{"kind":"instance"|"catalog","id":"&lt;guid&gt;"}</c>; голый id
    /// читается толерантно как legacy = "catalog"/запись. Собственные поля переопределяют
    /// унаследованные (<see cref="MergeBaseObjects"/>). Guard по типу цели: instance — тот же комплект;
    /// catalog — запись обязана быть в скоп-поддереве документа (System / его Set / Section /
    /// Construction), иначе cross-subtree = утечка чужих данных. Cycle-guard — общий
    /// <paramref name="visited"/> (id глобально уникальны; связи instance→catalog однонаправленны).
    /// </summary>
    private async Task<JsonElement> ResolveDocumentBaseRefAsync(
        JsonElement ownData, ScopeChain scope, HashSet<Guid> visited, CancellationToken ct)
    {
        if (ownData.ValueKind != JsonValueKind.Object) return ownData;
        if (!ownData.TryGetProperty("_baseRef", out var baseRefEl)) return ownData;
        var (kind, baseId) = ParseBaseRef(baseRefEl);
        if (baseId is not { } id) return ownData;

        JsonElement baseData;
        if (kind == "instance")
        {
            if (!visited.Add(id)) return ownData; // цикл/самоссылка → без наследования
            var baseInstance = await db.DocumentInstances.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == id && i.DocumentSetId == scope.SetId, ct); // same-set guard
            if (baseInstance is null) return ownData; // не найден / другой комплект
            baseData = await ResolveDocumentBaseRefAsync(baseInstance.Requisites.RootElement, scope, visited, ct);
        }
        else // catalog — запись общих данных
        {
            var entry = await db.CommonDataEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
            if (entry is null || !scope.Contains(entry.Scope, entry.ScopeId)) return ownData; // scope-subtree guard
            baseData = await ResolveCommonDataEntryAsync(id, visited, ct); // entry→entry цепочка + свой visited
        }

        return baseData.ValueKind == JsonValueKind.Object ? MergeBaseObjects(baseData, ownData) : ownData;
    }

    /// Разбор "_baseRef": дискриминированный объект {kind,id} (issue #71) или голый id-строка
    /// (legacy = "catalog"/запись). Возвращает (kind, id|null); неизвестный kind → "catalog".
    private static (string kind, Guid? id) ParseBaseRef(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
            return ("catalog", Guid.TryParse(el.GetString(), out var g) ? g : null);
        if (el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("id", out var idEl) && Guid.TryParse(idEl.GetString(), out var gid))
        {
            var kind = el.TryGetProperty("kind", out var kEl) ? kEl.GetString() : null;
            return (kind == "instance" ? "instance" : "catalog", gid);
        }
        return ("catalog", null);
    }

    /// <summary>
    /// Слияние двух JSON-объектов для _baseRef-наследования: базовые поля первыми, собственные
    /// переопределяют их на верхнем уровне; ключ "_baseRef" исключается. Чистая функция —
    /// общая для наследования записей каталога и инстансов документов (issue #71).
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
