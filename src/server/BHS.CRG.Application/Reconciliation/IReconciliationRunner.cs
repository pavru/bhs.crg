using BHS.CRG.Domain.Reconciliation;

namespace BHS.CRG.Application.Reconciliation;

/// <summary>
/// Выполнение прогона сверки. Арифметику и сопоставление считает КОД — ИИ здесь нет и не будет:
/// модель в пути сравнения означала бы «прыгающий» от прогона к прогону отчёт, то есть потерю доверия
/// к рабочему продукту по исполнительной документации (риск P1 в issue #414).
///
/// Место для ИИ определено отдельно и позже: предложить пары для несопоставленных позиций, чтобы
/// человек подтвердил и решение персистилось.
/// </summary>
public interface IReconciliationRunner
{
    /// <summary>Прогоняет сверку и сохраняет прогон с находками. Ошибка источника не бросается
    /// наружу, а фиксируется в прогоне: пустой журнал молча — хуже, чем видимая неудача.</summary>
    Task<ReconciliationRun> RunAsync(Guid definitionId, CancellationToken ct = default);
}

/// <summary>Находка с наложенным человеческим решением и признаком устранения.</summary>
/// <param name="Resolved">Было расхождение в предыдущем прогоне, сейчас совпадение. Вычисляется из
/// истории, а не хранится: хранимое поле рано или поздно разошлось бы с историей.</param>
/// <param name="Decision">Решение по этому ключу, если человек его принимал. Переживает прогоны.</param>
public record FindingView(
    ReconciliationFinding Finding,
    bool Resolved,
    ReconciliationDecision? Decision);
