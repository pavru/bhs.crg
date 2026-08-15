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

        // 1-2) связи по всей цепочке комплекта, где узкий уровень побеждает широкий. Алгоритм общий
        //      (issue #624): тот же ответ обязан показывать срез для внешнего агента и системный
        //      набор «Материалы и документы качества».
        var byKey = (await MaterialQualityChain.WinnersForSetAsync(db, instance.DocumentSetId, ct))
            .ToDictionary(kv => kv.Key, kv => kv.Value.QualityDocumentId);
        if (byKey.Count == 0) return;

        // 3) нужные документы — в ТОЙ ЖЕ форме, что даёт развёрнутая instance-ссылка (issue #736):
        //    реквизиты плюс наименование, штамп типа и скан. Раньше здесь клались голые реквизиты,
        //    и один и тот же сертификат выглядел в контексте по-разному в зависимости от того, каким
        //    путём пришёл. Шаблон, написанный на «ссылочную» форму, молча давал пустоту на этой —
        //    в первую очередь на «.Скан», а печать сертификата приложением к акту идёт именно
        //    через связь материала.
        //
        //    Вложенные ссылки в реквизитах здесь НЕ разворачиваем, как и раньше: их разберёт второй
        //    проход резолвера (ResolveContextRefsAsync). Общая функция от этого не зависит — она
        //    только собирает форму.
        var docIds = byKey.Values.Distinct().ToList();
        var docs = await db.QualityDocuments.AsNoTracking()
            .Where(d => docIds.Contains(d.Id))
            .ToListAsync(ct);
        var reqByDoc = docs.ToDictionary(d => d.Id, d => QualityDocShape.Build(
            d.Requisites.RootElement, d.DisplayName, d.DocumentTypeId,
            d.ScanBlobPath, d.ScanFileName, d.ScanMimeType));

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
