using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Common;

/// <summary>
/// Подъём по оси расположения: «уровень → его раздел и стройка» (комплект → раздел → стройка).
/// Неразрешённое звено — null: комплекта или раздела нет, или уровень — «вся система».
///
/// <para>Правило «где стройка у этого уровня» одно на слой приложения: им ходят
/// <c>/api/common-data/for-scope</c> и «своя стройка» подсказок поля (ревью PR #1585). Вторая копия
/// отстала бы молча — поменяй правило (скажем, комплект привяжут к стройке напрямую), и «своя стройка»
/// подсказок разошлась бы с for-scope без единого красного теста. Двойник инфраструктуры —
/// <c>ScopeChains</c>: он идёт тем же путём, но прямо по контексту базы.</para>
///
/// <para>Спуск в обратную сторону — <see cref="IScopeSubtree" />.</para>
/// </summary>
public readonly record struct ScopeAncestry(Guid? SetId, Guid? SectionId, Guid? ConstructionId)
{
    public static async Task<ScopeAncestry> ResolveAsync(
        CatalogScope? scope, Guid? scopeId, IRepository<DocumentSet> sets, IRepository<Section> sections,
        CancellationToken ct = default)
    {
        switch (scope)
        {
            case CatalogScope.Set when scopeId is { } setId:
                var set = await sets.GetByIdAsync(setId, ct);
                var sectionOfSet = set is null ? null : await sections.GetByIdAsync(set.SectionId, ct);
                return new(setId, set?.SectionId, sectionOfSet?.ConstructionId);
            case CatalogScope.Section when scopeId is { } sectionId:
                return new(null, sectionId, (await sections.GetByIdAsync(sectionId, ct))?.ConstructionId);
            case CatalogScope.Construction when scopeId is { } constructionId:
                return new(null, null, constructionId);
            default: // System — родителей нет.
                return default;
        }
    }
}
