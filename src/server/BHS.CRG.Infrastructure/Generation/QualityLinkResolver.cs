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
    public async Task InjectAsync(GenerationContext ctx, DocumentView instance, CancellationToken ct = default)
    {
        // Поля идентичности материала и целевое поле ссылки берём по функциональным тэгам
        // (material.identity / material.qualityDocLink) из составных типов, а не по именам.
        // Все типы, а не только составные: цепочка наследования разрешается по списку, и обрыв
        // цепочки молча отнял бы у подтипа унаследованные тэги.
        var allTypes = await db.DocumentTypes.AsNoTracking().ToListAsync(ct);

        // Только у типов, способных нести документ качества (issue #569): тэг identity носят и
        // единица измерения, и организация, а сопоставление по «шт» приклеило бы один сертификат
        // ко всем материалам с этой единицей.
        var identityFields = MaterialIdentity.KeysOf(allTypes);
        var targetField = MaterialIdentity.QualityDocFieldOf(allTypes);

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

        // 4) проход по контексту: каждому объекту с совпавшей идентичностью проставляем
        //    TargetField = реквизиты документа (вложенные $ref разрешит второй проход).
        //    Сам обход — в MaterialQualityInjector: он рекурсивный, потому что материалы бывают
        //    не только массивом верхнего уровня (union-обёртка АОСР, issue #648).
        foreach (var key in ctx.Data.Keys.ToList())
        {
            if (ctx.Data[key] is not JsonElement el) continue;
            if (MaterialQualityInjector.TryInject(el, identityFields, targetField, byKey, reqByDoc, out var injected))
                ctx.Set(key, injected);
        }
    }
}
