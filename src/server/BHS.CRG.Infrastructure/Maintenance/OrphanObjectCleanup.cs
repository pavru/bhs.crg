using BHS.CRG.Application.Common;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Maintenance;

/// <summary>Осиротевшие записи — сколько, где и сколько из них ещё держат ссылками.</summary>
/// <param name="Objects">Документы и записи общих данных.</param>
/// <param name="QualityDocuments">Документы качества уровня комплекта/раздела/стройки.</param>
/// <param name="MaterialLinks">Связки материалов той же оси.</param>
/// <param name="WithData">Объектов с непустыми данными — их потеря заметна, в отличие от пустых профилей.</param>
/// <param name="Referenced">На стольких сирот ссылаются живые записи; такие не удаляются.</param>
public record OrphanCleanupReport(
    int Objects, int QualityDocuments, int MaterialLinks, int WithData, int Referenced)
{
    /// <summary>Сколько будет удалено: найденное за вычетом того, на что ещё ссылаются.</summary>
    public int Total => Objects + QualityDocuments + MaterialLinks - Referenced;
}

/// <summary>
/// Уборка записей, чьё место расположения больше не существует (issue #739).
///
/// <para>Как они появлялись: у оси расположения нет внешнего ключа — она полиморфна, — и база
/// уносила разделы за стройкой и комплекты за разделом, а всё, что на этой оси висит, оставляла.
/// Прикладной каскад был только у комплекта, да и тот знал лишь про <c>domain_objects</c>. Причина
/// закрыта там же, где заведена эта уборка, но след остаётся: на рабочей базе такие записи уже
/// были, а восстановление старой резервной копии способно привезти их снова — поэтому инструмент,
/// а не разовый SQL.</para>
///
/// <para><b>Сирота ≠ мусор.</b> На неё может ссылаться живая запись: ссылки резолвятся по
/// идентификатору, а не по месту, и такая ссылка работает по сей день. Удали мы её цель — рабочая
/// ссылка стала бы висячей, то есть уборка сделала бы ровно то, что вся эта работа предотвращает.
/// Поэтому держимые сироты остаются, а отчёт их считает отдельно.</para>
///
/// <para>Действие администратора с предварительным подсчётом (<c>dryRun</c>): удаление
/// окончательное, и увидеть числа до него важнее, чем сэкономить шаг.</para>
///
/// <para>Идемпотентна: повторный прогон на убранной базе находит ноль.</para>
/// </summary>
public class OrphanObjectCleanup(
    AppDbContext db,
    IRepository<DomainObject> objRepo,
    IRepository<QualityDocument> qualityRepo,
    IReferenceIndex refIndex)
{
    /// <param name="dryRun">Только посчитать, ничего не удаляя.</param>
    public async Task<OrphanCleanupReport> RunAsync(bool dryRun, CancellationToken ct = default)
    {
        // Отбор и подсчёт — проекциями, без сущностей: в domain_objects лежат многомегабайтные
        // JSONB (одни картинки в общих данных дают мегабайты), и сухой прогон на базе с сотней
        // сирот вытянул бы их все, да ещё и в трекер изменений. «Есть ли данные» считает база.
        var objectIds = await db.DomainObjects
            .Where(o => o.ScopeId != null
                && ((o.ScopeLevel == CatalogScope.Set && !db.DocumentSets.Any(s => s.Id == o.ScopeId!.Value))
                 || (o.ScopeLevel == CatalogScope.Section && !db.Sections.Any(s => s.Id == o.ScopeId!.Value))
                 || (o.ScopeLevel == CatalogScope.Construction && !db.Constructions.Any(c => c.Id == o.ScopeId!.Value))))
            .Select(o => o.Id)
            .ToListAsync(ct);

        // «Данные непусты» считает база отдельным запросом: в LINQ-проекцию это не выражается
        // (JsonDocument там не сравнить), а тянуть сам JSONB ради одного флага — то самое, чего
        // проекции и избегают. jsonb '{}' сравниваем как текст: у пустого объекта запись одна.
        // $$ вместо $: в запросе есть литерал '{}', и при одинарном $ он был бы принят за дыру
        // интерполяции. С двойным дырой считается только {{...}}.
        var withData = objectIds.Count == 0 ? 0 : await db.Database
            .SqlQuery<int>($$"""
                SELECT count(*)::int AS "Value" FROM domain_objects
                WHERE "Id" = ANY({{objectIds}}) AND "Data"::text <> '{}'
                """)
            .SingleAsync(ct);

        var qualityIds = await db.QualityDocuments
            .Where(d => d.ScopeId != null
                && ((d.Scope == CatalogScope.Set && !db.DocumentSets.Any(s => s.Id == d.ScopeId!.Value))
                 || (d.Scope == CatalogScope.Section && !db.Sections.Any(s => s.Id == d.ScopeId!.Value))
                 || (d.Scope == CatalogScope.Construction && !db.Constructions.Any(c => c.Id == d.ScopeId!.Value))))
            .Select(d => d.Id)
            .ToListAsync(ct);

        var linkIds = await db.MaterialQualityLinks
            .Where(l => l.ScopeId != null
                && ((l.Scope == CatalogScope.Set && !db.DocumentSets.Any(s => s.Id == l.ScopeId!.Value))
                 || (l.Scope == CatalogScope.Section && !db.Sections.Any(s => s.Id == l.ScopeId!.Value))
                 || (l.Scope == CatalogScope.Construction && !db.Constructions.Any(c => c.Id == l.ScopeId!.Value))))
            .Select(l => l.Id)
            .ToListAsync(ct);

        var candidates = objectIds.Concat(qualityIds).ToHashSet();
        var held = await DomainObjectReferences.FindHeldTargetsAsync(objRepo, qualityRepo, refIndex, candidates, ct);

        var report = new OrphanCleanupReport(
            Objects: objectIds.Count,
            QualityDocuments: qualityIds.Count,
            MaterialLinks: linkIds.Count,
            WithData: withData,
            Referenced: held.Count);

        if (!dryRun && report.Total > 0)
        {
            // ExecuteDelete — по той же причине, что и проекции выше: удалять, не загружая JSONB.
            // Порядок: связки, потом документы качества (их собственные связки уносит FK-каскад),
            // потом объекты (за ними каскадом идут фасета и generated_files).
            var objIds = objectIds.Where(id => !held.Contains(id)).ToList();
            var qIds = qualityIds.Where(id => !held.Contains(id)).ToList();

            await db.MaterialQualityLinks.Where(l => linkIds.Contains(l.Id)).ExecuteDeleteAsync(ct);
            await db.QualityDocuments.Where(d => qIds.Contains(d.Id)).ExecuteDeleteAsync(ct);
            await db.DomainObjects.Where(o => objIds.Contains(o.Id)).ExecuteDeleteAsync(ct);
        }

        return report;
    }
}
