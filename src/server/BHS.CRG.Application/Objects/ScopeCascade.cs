using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Application.Objects;

/// <summary>
/// Каскад удаления уровня расположения — комплекта, раздела, стройки (issue #739).
///
/// <para><b>Зачем прикладной каскад вообще.</b> Ось расположения полиморфна: <c>ScopeLevel</c> +
/// <c>ScopeId</c> указывают то на комплект, то на раздел, то на стройку, и одним внешним ключом её
/// не выразить. Поэтому база уносит разделы за стройкой и комплекты за разделом, а всё, что висит
/// на этой оси, — нет: это надо удалять руками. Пока так делал только обработчик комплекта,
/// удаление раздела или стройки оставляло сирот — невидимых в интерфейсе, но живых для сканов,
/// поиска и резервной копии.</para>
///
/// <para><b>На оси три таблицы, не одна.</b> Кроме <c>domain_objects</c> там живут документы
/// качества (у библиотеки есть уровень: рядом с System встречаются Set и Construction) и связки
/// материалов. На рабочей базе это 14 документов и 54 связки не-System уровня — то есть речь не о
/// теоретической полноте, а о том, что удаление комплекта оставляет за собой хвост.</para>
///
/// <para><b>Зачем guard.</b> Поштучные guard'ы (#71/#269/#735) не пускают удалить объект, на
/// который ссылаются. Каскад обходил их с фланга: те же объекты уходили пачкой, и ссылка на них
/// молча повисала. Правило здесь то же — «на что ссылаются, не удаляем», — но множеством:
/// держатели ВНУТРИ удаляемого поддерева не в счёт, они уходят вместе со ссылкой.</para>
/// </summary>
public interface IScopeCascade
{
    /// <summary>Что уйдёт при удалении уровня и кто держит ссылки на это извне.</summary>
    Task<ScopeCascadePlan> PlanAsync(CatalogScope scope, Guid scopeId, CancellationToken ct = default);

    /// <summary>
    /// Помечает содержимое плана на удаление. Не сохраняет: вызывающий удаляет ещё и сам уровень, и
    /// уйти оба должны одной транзакцией — иначе отказ на второй половине оставил бы стройку без
    /// документов или документы без стройки.
    /// </summary>
    void Remove(ScopeCascadePlan plan);

    /// <summary>Отказ, если на содержимое ссылаются извне. <paramref name="levelAccusative"/> —
    /// «комплект», «раздел», «стройку».</summary>
    void EnsureDeletable(ScopeCascadePlan plan, string levelAccusative);
}

/// <param name="Objects">Объекты поддерева.</param>
/// <param name="QualityDocuments">Документы качества, чья область — внутри поддерева.</param>
/// <param name="MaterialLinks">Связки материалов той же области.</param>
/// <param name="ExternalReferrers">Держатели ссылок вне поддерева. Не пусто → удалять нельзя.</param>
/// <param name="WorkPlanItems">
/// Позиции перечня работ этого уровня (ТЗ CORE-10, issue #964). Удаляются ПРИКЛАДНО, а не каскадом
/// внешнего ключа, хотя ключ на стройку и раздел каскадный.
///
/// <para>⚠️ Причина не в аккуратности, а в порядке: EF удаляет <c>domain_objects</c> РАНЬШЕ строек и
/// разделов, и запись классификатора, лежащая на уровне стройки, уходит до того, как база унесёт
/// ссылающуюся на неё позицию. Ключ на вид работы — RESTRICT, поэтому сохранение падает
/// <c>23001</c>, и человек видит внутреннюю ошибку сервера вместо удаления стройки. Нашло ревью
/// PR #1056 — воспроизведением на живой базе, а не разбором.</para>
/// </param>
/// <param name="ModuleHolders">
/// Кто в данных модулей держит то, что уходит с уровнем (ТЗ CORE-34.2, issue #1094): сам уровень и
/// уровни под ним, объекты и документы качества поддерева, позиции перечня работ (CORE-11,
/// issue #964). Без этого вопроса удаление стройки унесло бы её из-под разноски счетов, а перечень —
/// из-под плана и факта: правило «удаляем только то, на что не ссылаются» обходится удалением уровня
/// с фланга — тем же, каким оно однажды обошлось для объектов уровня (issue #739).
/// </param>
/// <param name="WorkPlanItemsHoldingObjects">
/// Сколько позиций перечня ИЗВНЕ поддерева ссылаются на его записи — на запись классификатора или
/// единицу измерения, лежащую на уровне стройки. Внешний ключ таких записей удалить не даст, и без
/// этой проверки человек увидел бы не отказ, а внутреннюю ошибку сервера.
/// </param>
public sealed record ScopeCascadePlan(
    IReadOnlyList<DomainObject> Objects,
    IReadOnlyList<QualityDocument> QualityDocuments,
    IReadOnlyList<MaterialQualityLink> MaterialLinks,
    IReadOnlyList<DomainObjectReferences.Referrer> ExternalReferrers,
    IReadOnlyList<WorkPlanItem> WorkPlanItems,
    RecordHoldings ModuleHolders,
    int WorkPlanItemsHoldingObjects);

/// <inheritdoc cref="IScopeCascade" />
public class ScopeCascade(
    IRepository<DomainObject> objRepo,
    IRepository<QualityDocument> qualityRepo,
    IRepository<MaterialQualityLink> linkRepo,
    IRepository<Section> sectionRepo,
    IRepository<WorkPlanItem> planRepo,
    IRecordHolders holders,
    IReferenceIndex refIndex,
    IScopeSubtree subtree) : IScopeCascade
{
    /// <summary>
    /// План удаления уровня.
    ///
    /// <para>Поддерево берётся у общего спуска (<see cref="IScopeSubtree"/>), а не своим запросом:
    /// он же отвечает счётчику замечаний и провайдерам системных наборов, и «что лежит под
    /// стройкой» должно значить везде одно и то же.</para>
    ///
    /// <para>Собираются ВСЕ уровни поддерева, а не только комплекты: у стройки это ещё общие данные
    /// её разделов и её собственные. Пропусти их — и сироты остались бы ровно там, где их труднее
    /// всего заметить, на верхних уровнях, где записей мало и никто не пересчитывает.</para>
    /// </summary>
    public async Task<ScopeCascadePlan> PlanAsync(CatalogScope scope, Guid scopeId, CancellationToken ct = default)
    {
        var setIds = (await subtree.SetIdsUnderAsync(scope, scopeId, ct)).ToList();
        var sectionIds = scope switch
        {
            CatalogScope.Section => [scopeId],
            CatalogScope.Construction => (await sectionRepo.FindAsync(s => s.ConstructionId == scopeId, ct))
                .Select(s => s.Id).ToList(),
            _ => new List<Guid>(),
        };
        var constructionIds = scope == CatalogScope.Construction ? new List<Guid> { scopeId } : [];

        // Предикат один и тот же по смыслу, но выписан трижды: общий хелпер в Expression не
        // транслируется в SQL — EF увидел бы вызов метода и отказался (или, хуже, увёл бы отбор
        // в память, вытащив три таблицы целиком).
        var objects = await objRepo.FindAsync(o => o.ScopeId != null
            && ((o.ScopeLevel == CatalogScope.Set && setIds.Contains(o.ScopeId.Value))
                || (o.ScopeLevel == CatalogScope.Section && sectionIds.Contains(o.ScopeId.Value))
                || (o.ScopeLevel == CatalogScope.Construction && constructionIds.Contains(o.ScopeId.Value))), ct);

        var quality = await qualityRepo.FindAsync(d => d.ScopeId != null
            && ((d.Scope == CatalogScope.Set && setIds.Contains(d.ScopeId.Value))
                || (d.Scope == CatalogScope.Section && sectionIds.Contains(d.ScopeId.Value))
                || (d.Scope == CatalogScope.Construction && constructionIds.Contains(d.ScopeId.Value))), ct);

        var links = await linkRepo.FindAsync(l => l.ScopeId != null
            && ((l.Scope == CatalogScope.Set && setIds.Contains(l.ScopeId.Value))
                || (l.Scope == CatalogScope.Section && sectionIds.Contains(l.ScopeId.Value))
                || (l.Scope == CatalogScope.Construction && constructionIds.Contains(l.ScopeId.Value))), ct);

        // Цели — И объекты, И документы качества поддерева. Одним множеством, потому что оно
        // работает в обе стороны: ссылка на любую из этих записей извне удаление запрещает, а
        // держатель, сам входящий в множество, не считается. Не включи мы сюда документы качества
        // уровня комплекта — сертификат, заведённый в этом же комплекте и ссылающийся на его
        // запись общих данных, объявил бы комплект неудаляемым, «сославшись извне» на самого себя.
        var targetIds = objects.Select(o => o.Id).Concat(quality.Select(d => d.Id)).ToHashSet();
        var referrers = await DomainObjectReferences.FindReferrersAsync(objRepo, qualityRepo, refIndex, targetIds, ct);

        // Позиции перечня этого уровня (ТЗ CORE-10): уносит их каскад базы по стройке и разделу, но
        // спросить держателей ссылок обязаны мы — у базы такого вопроса нет.
        var items = await planRepo.FindAsync(
            p => constructionIds.Contains(p.ConstructionId)
                 || (p.SectionId != null && sectionIds.Contains(p.SectionId.Value)), ct);
        var itemIds = items.Select(p => p.Id).ToList();

        // Данные модулей спрашиваются обо ВСЁМ, что уходит, одним вопросом: и о самих уровнях (счёт
        // разнесён на стройку), и об их содержимом (строка счёта стоит на записи, заведённой на
        // уровне стройки), и о позициях перечня.
        var leaving = targetIds.Concat(itemIds)
            .Concat(setIds).Concat(sectionIds).Concat(constructionIds).ToHashSet();
        var moduleHolders = await holders.FindAsync(leaving, ct);

        // И обратная сторона: на запись содержимого может ссылаться позиция ЧУЖОГО перечня — если
        // заказчик держит запись классификатора или единицу на уровне стройки. Свои позиции не в
        // счёт: они уходят этим же каскадом, вместе со ссылкой.
        var objectIds = objects.Select(o => o.Id).ToList();
        var held = objectIds.Count == 0
            ? 0
            : (await planRepo.FindAsync(
                    p => objectIds.Contains(p.WorkTypeId) || objectIds.Contains(p.UnitId), ct))
                .Count(p => !itemIds.Contains(p.Id));

        return new ScopeCascadePlan(objects, quality, links, referrers, items, moduleHolders, held);
    }

    /// <inheritdoc />
    public void Remove(ScopeCascadePlan plan)
    {
        // Позиции перечня — ПЕРВЫМИ и прикладно (ревью PR #1056). Каскад базы унёс бы их за стройкой
        // и разделом, но позже, чем EF удалит объекты уровня, — а позиция ссылается на запись
        // классификатора ключом RESTRICT. Порядок внутри одного сохранения решает EF, и «сначала
        // позиции, потом объекты» получается только если позиции удалить самим.
        foreach (var i in plan.WorkPlanItems) planRepo.Remove(i);

        // Порядок: связки, потом документы качества, потом объекты. Связки у документа качества
        // ушли бы и каскадом базы, но только у СВОЕГО: связка уровня комплекта, указывающая на
        // документ общей библиотеки, каскадом не покрыта — её уносим сами.
        foreach (var l in plan.MaterialLinks) linkRepo.Remove(l);
        foreach (var d in plan.QualityDocuments) qualityRepo.Remove(d);
        foreach (var o in plan.Objects) objRepo.Remove(o); // фасета + generated_files каскадируются в БД
    }

    /// <inheritdoc />
    public void EnsureDeletable(ScopeCascadePlan plan, string levelAccusative)
    {
        // Ссылки модулей — первыми: это самая дорогая из потерь здесь (счета потеряли бы стройку, а
        // план, факт и акты — позицию перечня, к которой относятся).
        plan.ModuleHolders.EnsureNone(levelAccusative);

        if (plan.WorkPlanItemsHoldingObjects > 0)
            throw new ConflictException(
                $"Нельзя удалить {levelAccusative}: на его записи ссылается перечень работ ДРУГИХ "
                + $"строек — позиций: {plan.WorkPlanItemsHoldingObjects}. Это записи классификатора "
                + "видов работ или единицы измерения, заведённые на этом уровне, а перечень чужой "
                + "стройки без них перестал бы что-либо означать. Сообщите о находке: переносить "
                + "записи между уровнями система пока не умеет, и разъять это без нас нечем.");

        if (plan.ExternalReferrers.Count == 0) return;
        // Уровень назван словом в винительном падеже, держатели — списком: ссылка может держаться и
        // вне удаляемого места (документ качества общей библиотеки), и без имени её негде искать.
        throw new ConflictException(
            $"Нельзя удалить {levelAccusative} — на содержимое ссылаются извне: "
            + string.Join(", ", plan.ExternalReferrers.Select(r => r.Label))
            + ". Сначала снимите эти ссылки.");
    }
}
