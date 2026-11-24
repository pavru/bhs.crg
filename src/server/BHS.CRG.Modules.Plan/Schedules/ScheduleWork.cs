using BHS.CRG.Domain.Common;

namespace BHS.CRG.Modules.Plan.Schedules;

internal sealed class ScheduleWork
{
    private ScheduleWork() { }

    public Guid Id { get; private init; }
    public Guid ScheduleId { get; private init; }
    public Guid PlanItemId { get; private init; }
    public Guid? SourceRowId { get; private init; }
    public Guid? SectionId { get; private init; }
    public string Name { get; private init; } = null!;
    public decimal? Volume { get; private set; }
    public DateOnly? Start { get; private set; }
    public DateOnly? End { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    internal static ScheduleWork Create(
        Guid schedule, Guid item, Guid? row, Guid? section, string name, decimal? volume) =>
        new()
        {
            Id = Guid.NewGuid(), ScheduleId = schedule, PlanItemId = item, SourceRowId = row, SectionId = section,
            Name = name, Volume = volume, UpdatedAt = DateTimeOffset.UtcNow,
        };

    internal static void ValidateDetails(decimal? volume, DateOnly? start, DateOnly? end)
    {
        if (volume is <= -1_000_000_000_000m or >= 1_000_000_000_000m || (volume is { } v && decimal.Round(v, 6) != v))
            throw new InvalidRequestException("Объём работы должен содержать до двенадцати целых и шести дробных знаков.");
        if (start > end)
            throw new InvalidRequestException("Окончание работы не может быть раньше начала.");
    }

    internal void Edit(decimal? volume, DateOnly? start, DateOnly? end)
    {
        ValidateDetails(volume, start, end);
        Volume = volume;
        Start = start;
        End = end;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    internal void Rebuild(decimal? volume)
    {
        Volume = volume;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

internal sealed record ScheduleWorkAssignee(Guid WorkId, Guid EmployeeId);
