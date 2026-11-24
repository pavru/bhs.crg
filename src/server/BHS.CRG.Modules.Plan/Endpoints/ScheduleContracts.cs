using System.Text.Json.Serialization;

namespace BHS.CRG.Modules.Plan.Endpoints;

internal sealed record ScheduleCreate(Guid ConstructionId, Guid? RevisionId, Guid CreationKey, string? Name);
internal sealed record ScheduleWorkInput(decimal? Volume, DateOnly? Start, DateOnly? End, IReadOnlyList<Guid>? EmployeeIds);
internal sealed record ScheduleManualWork(Guid WorkTypeId, Guid? SectionId, ScheduleWorkInput Work);
internal sealed record ScheduleRebuildRequest(string? Fingerprint);
internal sealed record ScheduleLinkInput(Guid PredecessorId, Guid SuccessorId, string? Kind, int Lag);
internal sealed record ScheduleLinkDto(Guid Id, Guid PredecessorId, Guid SuccessorId, string Kind, int Lag, string Version);
internal sealed record ScheduleCycleWork(Guid Id, string Name, Guid ScheduleId, string ScheduleName);

internal sealed record ScheduleMark(string Code, string Text);
internal sealed record ScheduleWarning(string Code, string Text, Guid WorkId, Guid? LinkId = null, int? Year = null);
internal sealed record ScheduleSkipped(int Unmapped);
internal sealed record ScheduleSummary(Guid Id, Guid ConstructionId, Guid? RevisionId, string Name, string Version);
internal sealed record ScheduleDto(
    Guid Id, Guid ConstructionId, Guid? RevisionId, string Name, string Version, ScheduleSkipped Skipped,
    IReadOnlyList<ScheduleMark> Marks, IReadOnlyList<ScheduleWorkDto> Works, IReadOnlyList<ScheduleLinkDto> Links,
    IReadOnlyList<ScheduleWarning> Warnings);
internal sealed record ScheduleWorkDto(
    Guid Id, Guid PlanItemId, Guid WorkTypeId, Guid UnitId, Guid? SourceRowId, Guid? SectionId, string Name,
    decimal? Volume, DateOnly? Start, DateOnly? End, IReadOnlyList<Guid> EmployeeIds, bool Manual, string Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ScheduleWarning>? Warnings);

internal sealed record ScheduleDifference(Guid? WorkId, Guid RowId, Guid? SectionId, string Name, decimal? Volume);
internal sealed record ScheduleVolumeChange(
    Guid WorkId, Guid RowId, Guid? SectionId, string Name, decimal? Before, decimal? After);
internal sealed record SchedulePreview(string Version, string Fingerprint,
    IReadOnlyList<ScheduleDifference> Added, IReadOnlyList<ScheduleDifference> Removed,
    IReadOnlyList<ScheduleVolumeChange> Changed, ScheduleSkipped Skipped);
