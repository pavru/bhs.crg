using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// C#-аналог NewElementResolverStyles.xsl: разрешает ссылки entityRefs и $ref-объекты в реквизитах,
/// подмешивает данные сущностей каталога в контекст генерации.
/// </summary>
public class EntityResolver(AppDbContext db) : IEntityResolver
{
    public async Task<GenerationContext> ResolveAsync(DocumentInstance instance, CancellationToken ct = default)
    {
        var ctx = GenerationContext.FromJson(instance.Requisites, instance.EntityRefs, instance.PluginData);

        // 1. Резолвинг $ref-объектов в реквизитах
        await ResolveRefsAsync(ctx, instance.DocumentSetId, ct, nested: false);

        // 2. Резолвинг entityRefs (legacy catalog references)
        foreach (var prop in instance.EntityRefs.RootElement.EnumerateObject())
        {
            if (!Guid.TryParse(prop.Value.GetString(), out var entityId))
                continue;

            var entity = await db.CatalogEntities
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == entityId, ct);

            if (entity is null) continue;

            var merged = MergeEntityData(ctx, prop.Name, entity.Data);
            ctx.Set(prop.Name, merged);
        }

        return ctx;
    }

    private async Task ResolveRefsAsync(GenerationContext ctx, Guid documentSetId, CancellationToken ct, bool nested = false)
    {
        var keys = ctx.Data.Keys.ToList();
        foreach (var key in keys)
        {
            if (ctx.Data[key] is not JsonElement el) continue;

            // Массив элементов
            if (el.ValueKind == JsonValueKind.Array && !nested)
            {
                var elements = el.EnumerateArray().ToList();
                if (elements.Count == 0) continue;

                // Каждый элемент обрабатывается по своей структуре независимо от остальных
                var resolvedItems = new List<JsonElement>();
                foreach (var elem in elements)
                {
                    var r = await ResolveArrayElementAsync(elem, documentSetId, ct);
                    if (r.ValueKind != JsonValueKind.Undefined)
                        resolvedItems.Add(r);
                }
                ctx.Set(key, JsonSerializer.SerializeToElement(resolvedItems));
                continue;
            }

            if (el.ValueKind != JsonValueKind.Object) continue;
            if (!el.TryGetProperty("$ref", out var refTypeProp)) continue;
            var refType = refTypeProp.GetString();

            if (refType == "catalog" && el.TryGetProperty("entryId", out var entryIdProp)
                && Guid.TryParse(entryIdProp.GetString(), out var entryId))
            {
                var resolved = await ResolveCommonDataEntryAsync(entryId, [], ct);
                if (resolved.ValueKind != JsonValueKind.Undefined)
                {
                    resolved = await ResolveObjectRefsAsync(resolved, documentSetId, ct);
                    ctx.Set(key, resolved);
                }
            }
            else if (refType == "document"
                && el.TryGetProperty("instanceId", out var instIdProp)
                && Guid.TryParse(instIdProp.GetString(), out var instId)
                && el.TryGetProperty("fieldKey", out var fieldKeyProp))
            {
                var fieldKey = fieldKeyProp.GetString() ?? string.Empty;
                var refInstance = await db.DocumentInstances
                    .AsNoTracking()
                    .FirstOrDefaultAsync(i => i.Id == instId && i.DocumentSetId == documentSetId, ct);
                if (refInstance is not null
                    && refInstance.Requisites.RootElement.TryGetProperty(fieldKey, out var fieldVal))
                {
                    ctx.Set(key, fieldVal.Clone());
                }
            }
            // Ссылка на DocumentInstance (doc-ref): разворачиваем реквизиты на глубину 1
            else if (refType == "instance" && !nested
                && el.TryGetProperty("instanceId", out var docInstIdProp)
                && Guid.TryParse(docInstIdProp.GetString(), out var docInstId))
            {
                var resolved = await ResolveDocumentInstanceAsync(docInstId, documentSetId, ct);
                if (resolved.ValueKind != JsonValueKind.Undefined)
                    ctx.Set(key, resolved);
            }
        }
    }

    /// <summary>
    /// Обрабатывает один элемент массива: если сам является $ref — разворачивает его,
    /// иначе раскрывает $ref-свойства внутри объекта.
    /// </summary>
    private async Task<JsonElement> ResolveArrayElementAsync(JsonElement elem, Guid documentSetId, CancellationToken ct)
    {
        if (elem.ValueKind != JsonValueKind.Object)
            return elem.Clone();

        if (!elem.TryGetProperty("$ref", out var refProp))
            return await ResolveObjectRefsAsync(elem, documentSetId, ct);

        var refType = refProp.GetString();

        if (refType == "instance"
            && elem.TryGetProperty("instanceId", out var instIdProp)
            && Guid.TryParse(instIdProp.GetString(), out var instId))
        {
            var resolved = await ResolveDocumentInstanceAsync(instId, documentSetId, ct);
            return resolved.ValueKind != JsonValueKind.Undefined ? resolved : elem.Clone();
        }

        if (refType == "catalog"
            && elem.TryGetProperty("entryId", out var entryIdProp)
            && Guid.TryParse(entryIdProp.GetString(), out var entryId))
        {
            var resolved = await ResolveCommonDataEntryAsync(entryId, [], ct);
            if (resolved.ValueKind != JsonValueKind.Undefined)
                resolved = await ResolveObjectRefsAsync(resolved, documentSetId, ct);
            return resolved.ValueKind != JsonValueKind.Undefined ? resolved : elem.Clone();
        }

        return elem.Clone();
    }

    /// <summary>
    /// Раскрывает $ref-свойства внутри одного объекта (элемент составного массива).
    /// </summary>
    private async Task<JsonElement> ResolveObjectRefsAsync(JsonElement obj, Guid documentSetId, CancellationToken ct)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var prop in obj.EnumerateObject())
        {
            var val = prop.Value;
            if (val.ValueKind == JsonValueKind.Object && val.TryGetProperty("$ref", out var refTypeProp))
            {
                var refType = refTypeProp.GetString();
                if (refType == "catalog"
                    && val.TryGetProperty("entryId", out var entryIdProp)
                    && Guid.TryParse(entryIdProp.GetString(), out var entryId))
                {
                    var resolved = await ResolveCommonDataEntryAsync(entryId, [], ct);
                    dict[prop.Name] = resolved.ValueKind != JsonValueKind.Undefined ? resolved : val.Clone();
                }
                else if (refType == "instance"
                    && val.TryGetProperty("instanceId", out var instIdProp)
                    && Guid.TryParse(instIdProp.GetString(), out var instId))
                {
                    var resolved = await ResolveDocumentInstanceAsync(instId, documentSetId, ct);
                    dict[prop.Name] = resolved.ValueKind != JsonValueKind.Undefined ? resolved : val.Clone();
                }
                else
                {
                    dict[prop.Name] = val.Clone();
                }
            }
            else
            {
                dict[prop.Name] = val.Clone();
            }
        }
        return JsonSerializer.SerializeToElement(dict);
    }

    /// <summary>
    /// Загружает DocumentInstance и разворачивает его реквизиты на глубину 1
    /// (каталожные ссылки резолвятся, но вложенные doc-ref — нет, чтобы избежать циклов).
    /// </summary>
    private async Task<JsonElement> ResolveDocumentInstanceAsync(Guid instanceId, Guid documentSetId, CancellationToken ct)
    {
        var instance = await db.DocumentInstances
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == instanceId && i.DocumentSetId == documentSetId, ct);

        if (instance is null) return default;

        var subCtx = GenerationContext.FromJson(instance.Requisites, instance.EntityRefs, instance.PluginData);
        // nested=true: дальнейшие instance-ссылки не разворачиваем (защита от циклов)
        await ResolveRefsAsync(subCtx, documentSetId, ct, nested: true);

        var dict = new Dictionary<string, JsonElement>();
        foreach (var (k, v) in subCtx.Data)
            if (v is JsonElement je) dict[k] = je;

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

    private static JsonElement MergeEntityData(GenerationContext ctx, string key, JsonDocument entityData)
    {
        if (ctx.Data.TryGetValue(key, out var existing) && existing is JsonElement existingEl
            && existingEl.ValueKind == JsonValueKind.Object)
        {
            var merged = new Dictionary<string, JsonElement>();
            foreach (var p in entityData.RootElement.EnumerateObject())
                merged[p.Name] = p.Value.Clone();
            foreach (var p in existingEl.EnumerateObject())
                merged[p.Name] = p.Value.Clone();

            return JsonSerializer.SerializeToElement(merged);
        }

        return entityData.RootElement.Clone();
    }
}
