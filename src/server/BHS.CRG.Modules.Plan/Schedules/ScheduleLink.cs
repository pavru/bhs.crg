namespace BHS.CRG.Modules.Plan.Schedules;

internal sealed class ScheduleLink
{
    internal const string FinishToStart = "fs";

    private ScheduleLink() { }

    public Guid Id { get; private init; }
    public Guid PredecessorId { get; private set; }
    public Guid SuccessorId { get; private set; }
    public string Kind { get; private set; } = null!;
    public int Lag { get; private set; }

    internal static ScheduleLink Create(Guid predecessor, Guid successor, string kind, int lag)
    {
        var link = new ScheduleLink { Id = Guid.NewGuid() };
        link.Change(predecessor, successor, kind, lag);
        return link;
    }

    internal void Change(Guid predecessor, Guid successor, string kind, int lag) =>
        (PredecessorId, SuccessorId, Kind, Lag) = (predecessor, successor, kind, lag);
}
