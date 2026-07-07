using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
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
        var ctx = GenerationContext.FromJson(instance.Requisites, instance.PluginData);
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

        // Merge: базовые поля первыми, собственные поля переопределяют их
        var merged = new Dictionary<string, JsonElement>();
        foreach (var p in baseData.EnumerateObject())
            if (p.Name != "_baseRef") merged[p.Name] = p.Value.Clone();
        foreach (var p in ownData.EnumerateObject())
            if (p.Name != "_baseRef") merged[p.Name] = p.Value.Clone();

        return JsonSerializer.SerializeToElement(merged);
    }

}
