using System.Text.Json.Serialization;
using BHS.CRG.Modules.Access;

namespace BHS.CRG.Modules.Exchange;

/// <summary>Выработка вида работ в единице классификатора (PLAN-16, WORK-57).</summary>
[ModuleContract("work", "Выработка")]
public interface IWorkProductivity
{
    /// <summary>Право ответа. Читается без обращения к данным.</summary>
    IReadOnlyList<string> AnswerPermissions { get; }

    /// <summary>Непересекающиеся месяцы экземпляра. Охват, сотрудники и частичные месяцы не задаются потребителем.</summary>
    Task<ContractAnswer<WorkProductivityAnswer>> ReadAsync(
        ModuleReader reader, WorkProductivityQuery query, CancellationToken ct = default);
}

public sealed record WorkProductivityQuery(Guid WorkTypeId, Guid UnitId);

public sealed record WorkProductivitySample(int Reports, int Constructions);

/// <summary>Выработка с порогом. При недостаточной выборке VolumePerHour отсутствует.</summary>
public sealed record WorkProductivityValue(string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? VolumePerHour,
    WorkProductivitySample Sample);

public sealed record WorkProductivityMonth(DateOnly Month, WorkProductivityValue Productivity);

public sealed record WorkProductivityAnswer(
    Guid UnitId, WorkProductivitySample MinSample, string State, IReadOnlyList<WorkProductivityMonth> Months);
