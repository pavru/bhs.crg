using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Reconciliation;
using MediatR;

namespace BHS.CRG.Application.Reconciliation;

/// <summary>Уровень иерархии, для которого спрашивают проблемы.</summary>
/// <param name="Scope">Construction / Section / Set.</param>
public record ProblemScope(CatalogScope Scope, Guid ScopeId);

/// <param name="Reconciliations">Сверки, относящиеся к уровню, с числом НЕРАЗОБРАННЫХ находок.</param>
/// <param name="UnresolvedFindings">Сумма по сверкам: находки без решения человека.</param>
/// <param name="UnreviewedObservations">Замечания агента, ждущие разбора.</param>
public record RelatedProblems(
    IReadOnlyList<RelatedReconciliation> Reconciliations,
    int UnresolvedFindings,
    int UnreviewedObservations)
{
    /// <summary>
    /// Что показывать в счётчике. Считаем только НЕРАЗОБРАННОЕ: бейдж обязан обнуляться действиями
    /// человека, иначе он перестаёт быть сигналом и становится украшением.
    /// </summary>
    public int NeedsAttention => UnresolvedFindings + UnreviewedObservations;

    /// <summary>
    /// Есть ли расхождение, посчитанное САМОЙ системой. Красный цвет зарезервирован за арифметикой:
    /// двести неразобранных утверждений агента — это не то же самое, что одно расхождение в числах.
    /// </summary>
    public bool HasArithmeticProblems => UnresolvedFindings > 0;
}

/// <param name="UnresolvedFindings">Находки без решения человека: расхождение либо отсутствие пары.</param>
public record RelatedReconciliation(Guid Id, string Name, int UnresolvedFindings, DateTimeOffset? LastRunAt);

public record GetRelatedProblemsQuery(CatalogScope Scope, Guid ScopeId) : IRequest<RelatedProblems>;

/// <param name="NeedsAttention">Сколько ждёт разбора человеком на этом уровне и ниже.</param>
/// <param name="HasArithmeticProblems">Есть ли расхождение, посчитанное системой (а не заявленное агентом).</param>
public record ProblemCount(Guid ScopeId, int NeedsAttention, bool HasArithmeticProblems);

/// <param name="Children">Разбивка по непосредственным детям уровня: разделы у стройки, комплекты у
/// раздела, стройки у System. Отдаём вместе со своим счётчиком, чтобы страница обходилась ОДНИМ
/// запросом — иначе раздел с десятью комплектами сделал бы десять.</param>
public record ProblemSummary(
    int NeedsAttention, bool HasArithmeticProblems, IReadOnlyList<ProblemCount> Children);

/// <param name="ScopeId">Пусто при <c>System</c>: детьми тогда становятся все стройки.</param>
public record GetProblemSummaryQuery(CatalogScope Scope, Guid? ScopeId) : IRequest<ProblemSummary>;

/// <summary>
/// Какие сверки относятся к уровню иерархии (issue #452).
///
/// Принадлежность — свойство СПЕКИ, а не находок: спека называет источники, и связь достраивается
/// пакетными запросами. Перебирать прогоны и находки ради счётчика не нужно.
///
/// Две оси, ОБЪЕДИНЕНИЕ:
///  • область файла набора, которому принадлежит источник;
///  • область объектов, привязанных к источнику через DataSetBinding (документ потребляет эти строки).
///
/// Правило «самый узкий scope» отвергнуто сознательно: сверка над источниками разных уровней
/// досталась бы одному комплекту и исчезла бы с раздела, хотя касается обоих.
///
/// System-область связи НЕ даёт: сверка над общесистемным файлом иначе загорелась бы на каждом
/// комплекте и обесценила бы счётчики за вечер. Не привязавшаяся никуда сверка остаётся «общей» —
/// см. <see cref="IProblemAttribution.GlobalReconciliationsAsync"/>, тихо исчезнуть она не должна.
/// </summary>
public interface IProblemAttribution
{
    /// <summary>Идентификаторы сверок, относящихся к уровню.</summary>
    Task<IReadOnlyList<Guid>> ReconciliationIdsForAsync(
        CatalogScope scope, Guid scopeId, CancellationToken ct = default);

    /// <summary>Сверки, не привязавшиеся ни к одному уровню, — общие для системы.</summary>
    Task<IReadOnlyList<Guid>> GlobalReconciliationsAsync(CancellationToken ct = default);

    // Спуск «уровень → комплекты поддерева» жил здесь же и был единственной приличной его копией
    // (issue #625). Теперь он общий — Application.Common.IScopeSubtree: обходить поддерево нужно и
    // списку доступных документов, и провайдерам системных наборов, а зависеть ради этого от
    // разбора проблем сверки им незачем.
}
