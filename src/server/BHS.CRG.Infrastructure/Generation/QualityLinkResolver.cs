using System.Text.Json;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Generation;

public class QualityLinkResolver(AppDbContext db) : IQualityLinkResolver
{
    public async Task InjectAsync(GenerationContext ctx, DocumentInstance instance, CancellationToken ct = default)
    {
        // Поля идентичности материала и целевое поле ссылки берём по функциональным тэгам
        // (material.identity / material.qualityDocLink) из составных типов, а не по именам.
        var composites = await db.DocumentTypes.AsNoTracking()
            .Where(t => t.Kind == DocumentTypeKind.Composite)
            .ToListAsync(ct);

        var identityFields = composites
            .SelectMany(t => SchemaTags.FieldKeysWithTag(t.Schema, FunctionalTag.MaterialIdentity))
            .Distinct().ToArray();
        var targetField = composites
            .SelectMany(t => SchemaTags.FieldKeysWithTag(t.Schema, FunctionalTag.MaterialQualityDocLink))
            .FirstOrDefault();

        if (identityFields.Length == 0 || targetField is null) return; // тэги не настроены — нечего подмешивать

        // 1) scope-цепочка комплекта
        var set = await db.DocumentSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == instance.DocumentSetId, ct);
        if (set is null) return;
        Guid sectionId = set.SectionId;
        Guid constructionId = Guid.Empty;
        var section = await db.Sections.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sectionId, ct);
        if (section is not null) constructionId = section.ConstructionId;

        // 2) связи по всей цепочке, приоритет — более узкий scope (Set=1 … System=5)
        var links = await db.MaterialQualityLinks.AsNoTracking()
            .Where(l =>
                (l.Scope == CatalogScope.Set && l.ScopeId == instance.DocumentSetId) ||
                (l.Scope == CatalogScope.Section && l.ScopeId == sectionId) ||
                (l.Scope == CatalogScope.Construction && l.ScopeId == constructionId) ||
                l.Scope == CatalogScope.System)
            .ToListAsync(ct);
        if (links.Count == 0) return;

        var byKey = new Dictionary<string, Guid>();
        foreach (var l in links.OrderBy(l => (int)l.Scope))
            byKey.TryAdd(l.MaterialKey, l.QualityDocumentId); // первый (более узкий scope) побеждает

        // 3) реквизиты нужных документов
        var docIds = byKey.Values.Distinct().ToList();
        var docs = await db.QualityDocuments.AsNoTracking()
            .Where(d => docIds.Contains(d.Id))
            .ToListAsync(ct);
        var reqByDoc = docs.ToDictionary(d => d.Id, d => d.Requisites.RootElement.Clone());

        // 4) проход по массивам контекста: для каждого элемента с совпавшей идентичностью
        //    проставляем TargetField = реквизиты документа (вложенные $ref разрешит второй проход)
        foreach (var key in ctx.Data.Keys.ToList())
        {
            if (ctx.Data[key] is not JsonElement el || el.ValueKind != JsonValueKind.Array) continue;

            var changed = false;
            var newItems = new List<JsonElement>();
            foreach (var elem in el.EnumerateArray())
            {
                if (elem.ValueKind != JsonValueKind.Object) { newItems.Add(elem.Clone()); continue; }

                if (TryMatch(elem, identityFields, byKey, out var docId)
                    && reqByDoc.TryGetValue(docId, out var reqs)
                    && !HasValue(elem, targetField)) // не перетираем заданное вручную
                {
                    var dict = new Dictionary<string, JsonElement>();
                    foreach (var p in elem.EnumerateObject()) dict[p.Name] = p.Value.Clone();
                    dict[targetField] = reqs;
                    newItems.Add(JsonSerializer.SerializeToElement(dict));
                    changed = true;
                }
                else
                {
                    newItems.Add(elem.Clone());
                }
            }
            if (changed) ctx.Set(key, JsonSerializer.SerializeToElement(newItems));
        }
    }

    // Материал может быть привязан по любому из полей идентичности (артикул ИЛИ наименование) —
    // проверяем все, т.к. ключ связи мог быть создан по любому из них.
    private static bool TryMatch(JsonElement elem, string[] identityFields,
        IReadOnlyDictionary<string, Guid> byKey, out Guid docId)
    {
        foreach (var field in identityFields)
            if (elem.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var key = MaterialKeyNormalizer.Normalize(v.GetString());
                if (key.Length > 0 && byKey.TryGetValue(key, out docId)) return true;
            }
        docId = default;
        return false;
    }

    private static bool HasValue(JsonElement elem, string field)
    {
        if (!elem.TryGetProperty(field, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(v.GetString()),
            JsonValueKind.Object => v.EnumerateObject().Any(),
            _ => true,
        };
    }
}
