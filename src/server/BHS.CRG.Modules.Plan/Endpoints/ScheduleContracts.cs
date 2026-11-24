namespace BHS.CRG.Modules.Plan.Endpoints;

internal sealed record ScheduleCreate(Guid ConstructionId, Guid? RevisionId, Guid CreationKey, string? Name);
internal sealed record ScheduleWorkInput(decimal? Volume, DateOnly? Start, DateOnly? End, IReadOnlyList<Guid>? EmployeeIds);
internal sealed record ScheduleManualWork(Guid WorkTypeId, Guid? SectionId, ScheduleWorkInput Work);
internal sealed record ScheduleRebuildRequest(string? Fingerprint);

internal sealed record ScheduleMark(string Code, string Text);
internal sealed record ScheduleSkipped(int Unmapped);
internal sealed record ScheduleSummary(Guid Id, Guid ConstructionId, Guid? RevisionId, string Name, string Version);
internal sealed record ScheduleDto(
    Guid Id, Guid ConstructionId, Guid? RevisionId, string Name, string Version, ScheduleSkipped Skipped,
    IReadOnlyList<ScheduleMark> Marks, IReadOnlyList<ScheduleWorkDto> Works);
internal sealed record ScheduleWorkDto(
    Guid Id, Guid PlanItemId, Guid WorkTypeId, Guid UnitId, Guid? SourceRowId, Guid? SectionId, string Name,
    decimal? Volume, DateOnly? Start, DateOnly? End, IReadOnlyList<Guid> EmployeeIds, bool Manual, string Version);

internal sealed record ScheduleDifference(Guid? WorkId, Guid RowId, Guid? SectionId, string Name, decimal? Volume);
internal sealed record ScheduleVolumeChange(
    Guid WorkId, Guid RowId, Guid? SectionId, string Name, decimal? Before, decimal? After);
internal sealed record SchedulePreview(string Version, string Fingerprint,
    IReadOnlyList<ScheduleDifference> Added, IReadOnlyList<ScheduleDifference> Removed,
    IReadOnlyList<ScheduleVolumeChange> Changed, ScheduleSkipped Skipped);
