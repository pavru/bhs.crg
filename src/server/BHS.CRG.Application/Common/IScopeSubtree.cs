using BHS.CRG.Domain.Catalog;

namespace BHS.CRG.Application.Common;

/// <summary>
/// Спуск по оси расположения: «уровень → все комплекты под ним» (issue #625).
///
/// Обратная сторона <c>ScopeChains</c> (тот идёт ВВЕРХ: комплект → раздел → стройка). Спуск был
/// продублирован в трёх местах, и лучшая из копий сидела внутри <see cref="Reconciliation.IProblemAttribution"/> —
/// то есть чтобы обойти поддерево, надо было зависеть от службы разбора проблем сверки.
///
/// Здесь только контракт: сам обход — запрос к хранилищу и живёт в инфраструктуре.
/// </summary>
public interface IScopeSubtree
{
    /// <summary>
    /// Идентификаторы комплектов под уровнем: Set — он сам, Section — его комплекты, Construction —
    /// комплекты всех его разделов. System и уровень без идентификатора дают пустой список: «все
    /// комплекты базы» — не поддерево, и вызывающему, который спросил про несуществующее место,
    /// честнее получить пусто, чем всё.
    /// </summary>
    Task<IReadOnlyList<Guid>> SetIdsUnderAsync(CatalogScope scope, Guid? scopeId, CancellationToken ct = default);
}
