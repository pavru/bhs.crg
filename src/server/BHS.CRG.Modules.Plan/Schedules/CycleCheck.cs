namespace BHS.CRG.Modules.Plan.Schedules;

internal readonly record struct ScheduleEdge(Guid PredecessorId, Guid SuccessorId);

internal static class CycleCheck
{
    // Добавление A → B образует цикл, когда из B уже можно дойти до A.
    internal static IReadOnlyList<Guid> Find(IReadOnlyList<ScheduleEdge> existing, Guid predecessor, Guid successor)
    {
        var byPredecessor = existing.ToLookup(edge => edge.PredecessorId, edge => edge.SuccessorId);
        var via = new Dictionary<Guid, Guid> { [successor] = successor };
        var next = new Queue<Guid>();
        next.Enqueue(successor);

        while (next.TryDequeue(out var work))
        {
            if (work == predecessor)
            {
                var path = new List<Guid> { predecessor };
                while (work != successor)
                {
                    work = via[work];
                    path.Add(work);
                }
                path.Add(predecessor);
                path.Reverse();
                return path;
            }
            foreach (var follower in byPredecessor[work])
                if (via.TryAdd(follower, work)) next.Enqueue(follower);
        }

        return [];
    }
}
