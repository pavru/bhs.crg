using BHS.CRG.Modules.Access;

namespace BHS.CRG.Modules.Exchange;

/// <summary>Текущие принятые объёмы по позициям перечня (CORE-34, WORK-57).</summary>
[ModuleContract("work", "Фактические работы")]
public interface IActualWorks
{
    /// <summary>Права, любое из которых открывает ответ. Первое называется при отказе. База не читается.</summary>
    IReadOnlyList<string> AnswerPermissions { get; }

    Task<ContractAnswer<ActualWorksAnswer>> ReadAsync(
        ModuleReader reader, ActualWorksQuery query, CancellationToken ct = default);
}

/// <summary>
/// Стройка и включительные границы дат смен. Пустой раздел или список <c>null</c> означают отсутствие
/// соответствующего отбора. Пустой список позиций исключает все объёмы. Отборы не сужают диагностику.
/// </summary>
public sealed record ActualWorksQuery(
    Guid ConstructionId, Guid? SectionId = null, IReadOnlyCollection<Guid>? PlanItemIds = null,
    DateOnly? From = null, DateOnly? To = null);

/// <summary>Объёмы и исключения из текущего факта. Полнота оценивается с учётом охвата диагностики.</summary>
public sealed record ActualWorksAnswer(IReadOnlyList<ActualWork> Items, ActualWorksDiagnostics Diagnostics);

/// <summary>Объём в единице позиции. Дата из отчёта, раздел <c>null</c> означает «стройка в целом».</summary>
public sealed record ActualWork(Guid PlanItemId, DateOnly Date, string Shift, Guid? SectionId, decimal Volume);

/// <summary>Диагностика всей стройки за включительный диапазон дат. Границы могут быть открыты.</summary>
public sealed record ActualWorksScope(Guid ConstructionId, DateOnly? From, DateOnly? To);

/// <summary>Основная причина исключения непустой бригадной группы.</summary>
public enum CrewFactExclusion
{
    NotApproved = 1,
    Stale,
    AwaitingAcceptance,
    InvalidKey,
    MultipleKeys,
}

/// <summary>Число исключённых групп с одной основной причиной.</summary>
public sealed record CrewFactExclusionCount(CrewFactExclusion Reason, int Groups);

/// <param name="Scope">Стройка и даты до отбора по разделу или позициям.</param>
/// <param name="AwaitingAcceptance">Число различных поданных отчётов, ожидающих приёмки.</param>
/// <param name="ExcludedCrewGroups">Число непустых бригадных групп, не включённых в факт.</param>
/// <param name="CrewExclusions">Разбиение исключённых групп по одной основной причине.</param>
/// <param name="WithoutPlanItem">Число принятых строк с негодной ссылкой на позицию перечня.</param>
/// <param name="Unsubmitted">Предупреждение. Число пар «группа, неподанный участник».</param>
public sealed record ActualWorksDiagnostics(
    ActualWorksScope Scope, int AwaitingAcceptance, int ExcludedCrewGroups,
    IReadOnlyList<CrewFactExclusionCount> CrewExclusions, int WithoutPlanItem, int Unsubmitted)
{
    /// <summary>Неподанные участники — предупреждение. Неполноту определяют исключения из факта.</summary>
    public bool IsPartial() => AwaitingAcceptance > 0 || ExcludedCrewGroups > 0 || WithoutPlanItem > 0;
}
