using System.Text.Json.Serialization;
using BHS.CRG.Modules.Exchange;

namespace BHS.CRG.Modules.Plan.Endpoints;

internal sealed record ScheduleCreate(Guid ConstructionId, Guid? RevisionId, Guid CreationKey, string? Name);
internal sealed record ScheduleWorkInput(decimal? Volume, DateOnly? Start, DateOnly? End, IReadOnlyList<Guid>? EmployeeIds);
internal sealed record ScheduleManualWork(Guid WorkTypeId, Guid? SectionId, ScheduleWorkInput Work);
internal sealed record ScheduleRebuildRequest(string? Fingerprint);
internal sealed record ScheduleLinkInput(Guid PredecessorId, Guid SuccessorId, string? Kind, int Lag);
internal sealed record ScheduleLinkDto(Guid Id, Guid PredecessorId, Guid SuccessorId, string Kind, int Lag, string Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScheduleWorkRef? Predecessor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScheduleWorkRef? Successor = null);
internal sealed record ScheduleWorkRef(Guid Id, string Name, Guid ScheduleId, string ScheduleName, Guid? SectionId);
internal sealed record ScheduleRemovedLink(Guid Id, ScheduleWorkRef Predecessor, ScheduleWorkRef Successor, string Kind, int Lag);

internal sealed record ScheduleMark(string Code, string Text);
internal sealed record ScheduleWarning(string Code, string Text, Guid WorkId, Guid? LinkId = null, int? Year = null);
internal sealed record ScheduleSkipped(int Unmapped);
internal sealed record ScheduleSummary(Guid Id, Guid ConstructionId, Guid? RevisionId, string Name, string Version);
internal sealed record ScheduleAssigneeDto(Guid Id, string Name, bool Archived);
internal sealed record ScheduleDto(
    Guid Id, Guid ConstructionId, Guid? RevisionId, string Name, string Version, ScheduleSkipped Skipped,
    IReadOnlyList<ScheduleMark> Marks, IReadOnlyList<ScheduleWorkDto> Works, IReadOnlyList<ScheduleLinkDto> Links,
    IReadOnlyList<ScheduleWarning> Warnings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActualWorksDiagnostics? FactDiagnostics = null);
internal sealed record ScheduleWorkDto(
    Guid Id, Guid PlanItemId, Guid WorkTypeId, Guid UnitId, Guid? SourceRowId, Guid? SectionId, string Name,
    decimal? Volume, DateOnly? Start, DateOnly? End, IReadOnlyList<Guid> EmployeeIds, bool Manual, string Version,
    int? DurationWorkingDays, string? UnitName, IReadOnlyList<ScheduleAssigneeDto> Assignees,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ScheduleWarning>? Warnings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScheduleWorkFact? Fact = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScheduleWorkProductivity? Productivity = null);

internal sealed record ScheduleWorkFact(string State, decimal? Volume = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Permission = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null);

internal sealed record ScheduleWorkProductivity(string State,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorkProductivityMonth>? Months = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkProductivitySample? MinSample = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Permission = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null);

internal sealed record ScheduleDifference(Guid? WorkId, Guid RowId, Guid? SectionId, string Name, decimal? Volume);
internal sealed record ScheduleVolumeChange(
    Guid WorkId, Guid RowId, Guid? SectionId, string Name, decimal? Before, decimal? After);
internal sealed record SchedulePreview(string Version, string Fingerprint,
    IReadOnlyList<ScheduleDifference> Added, IReadOnlyList<ScheduleDifference> Removed,
    IReadOnlyList<ScheduleVolumeChange> Changed, ScheduleSkipped Skipped, IReadOnlyList<ScheduleRemovedLink> RemovedLinks);
