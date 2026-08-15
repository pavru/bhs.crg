using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Application.Objects;

/// <summary>
/// Каскад удаления уровня расположения — комплекта, раздела, стройки (issue #739).
///
/// <para><b>Зачем прикладной каскад вообще.</b> У <c>DomainObject</c> нет внешнего ключа на
/// комплект: ось расположения полиморфна (<c>ScopeLevel</c> + <c>ScopeId</c> указывают то на
/// комплект, то на раздел, то на стройку), и одним FK её не выразить. Поэтому база уносит
/// разделы за стройкой и комплекты за разделом, а объекты — нет: их надо удалять руками. Пока
/// это делал только обработчик комплекта, удаление раздела или стройки оставляло документы и
/// записи общих данных сиротами — невидимыми в интерфейсе, но живыми для сканов, поиска и
/// резервной копии. На рабочей базе такие уже были.</para>
///
/// <para><b>Зачем guard.</b> Поштучные guard'ы (#71/#269/#735) не пускают удалить объект, на
/// который ссылаются. Каскад обходил их с фланга: те же объекты уходили пачкой, и ссылка на них
/// молча повисала. Правило здесь то же — «на что ссылаются, не удаляем», — но множеством:
/// держатели ВНУТРИ удаляемого поддерева не в счёт, они уходят вместе со ссылкой.</para>
/// </summary>
public static class ScopeCascade
{
    /// <summary>Что уйдёт при удалении уровня и кто держит ссылки на это извне.</summary>
    /// <param name="Objects">Объекты поддерева — их удаляет вызывающий.</param>
    /// <param name="ExternalReferrers">Держатели ссылок вне поддерева. Не пусто → удалять нельзя.</param>
    public sealed record Plan(
        IReadOnlyList<DomainObject> Objects,
        IReadOnlyList<DomainObjectReferences.Referrer> ExternalReferrers);

    /// <summary>
    /// План удаления уровня <paramref name="scope"/>/<paramref name="scopeId"/>.
    ///
    /// <para>Поддерево берётся у общего спуска (<see cref="IScopeSubtree"/>), а не своим запросом:
    /// он же отвечает счётчику замечаний и провайдерам системных наборов, и «что лежит под
    /// стройкой» должно значить везде одно и то же.</para>
    ///
    /// <para>Объекты собираются по ВСЕМ уровням поддерева, а не только по комплектам: у стройки
    /// это ещё общие данные её разделов и её собственные. Пропусти их — и сироты остались бы
    /// ровно там, где их труднее всего заметить, на верхних уровнях, где записей мало и никто не
    /// пересчитывает.</para>
    /// </summary>
    public static async Task<Plan> PlanAsync(
        IRepository<DomainObject> objRepo,
        IRepository<QualityDocument> qualityRepo,
        IRepository<Section> sectionRepo,
        IScopeSubtree subtree,
        CatalogScope scope, Guid scopeId, CancellationToken ct)
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

        var objects = await objRepo.FindAsync(o =>
            o.ScopeId != null
            && ((o.ScopeLevel == CatalogScope.Set && setIds.Contains(o.ScopeId.Value))
                || (o.ScopeLevel == CatalogScope.Section && sectionIds.Contains(o.ScopeId.Value))
                || (o.ScopeLevel == CatalogScope.Construction && constructionIds.Contains(o.ScopeId.Value))), ct);

        var ids = objects.Select(o => o.Id).ToHashSet();
        var referrers = await DomainObjectReferences.FindReferrersAsync(objRepo, qualityRepo, ids, ct);
        return new Plan(objects, referrers);
    }

    /// <summary>
    /// Текст отказа. Уровень назван словом в винительном падеже («комплект», «раздел», «стройку»),
    /// а держатели — списком: ссылка может держаться и вне удаляемого места (документ качества
    /// общей библиотеки), и без имени человеку негде её искать.
    /// </summary>
    public static string RefusalMessage(string levelAccusative, IReadOnlyList<DomainObjectReferences.Referrer> referrers)
        => $"Нельзя удалить {levelAccusative} — на содержимое ссылаются извне: "
           + string.Join(", ", referrers.Select(r => r.Label))
           + ". Сначала снимите эти ссылки.";
}
