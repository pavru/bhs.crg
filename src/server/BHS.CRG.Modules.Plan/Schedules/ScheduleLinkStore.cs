using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Schedules;

internal sealed class ScheduleLinkRefusal(
    string code, string message, IReadOnlyList<ScheduleWorkRef>? cycle = null) : InvalidRequestException(message)
{
    internal string Code => code;
    internal IReadOnlyList<ScheduleWorkRef>? Cycle => cycle;
}

internal sealed class ScheduleLinkStore(PlanDbContext db, ScheduleBuilder schedules)
{
    internal async Task<ScheduleLinkDto> WriteAsync(
        Guid id, Guid? linkId, ScheduleLinkInput input, HttpRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var schedule = await schedules.LockAsync(id, ct);
        var existing = linkId is { } row ? await OwnedAsync(id, row, request, ct) : null;
        if (input.Kind != ScheduleLink.FinishToStart)
            throw new ScheduleLinkRefusal("unsupported-kind", "Пока поддерживается только «окончание → начало».");

        var works = await (from work in db.ScheduleWorks
            join owner in db.Schedules on work.ScheduleId equals owner.Id
            where work.Id == input.PredecessorId || work.Id == input.SuccessorId
            select new { work.Id, work.ScheduleId, owner.ConstructionId }).ToDictionaryAsync(w => w.Id, ct);
        if (!works.TryGetValue(input.PredecessorId, out var predecessor) ||
            !works.TryGetValue(input.SuccessorId, out var successor))
            throw new NotFoundException("Предшественник или последователь не найден.");
        if (predecessor.ConstructionId != schedule.ConstructionId || successor.ConstructionId != schedule.ConstructionId)
            throw new ScheduleLinkRefusal("other-construction", "Связать можно только работы одной стройки.");
        if (successor.ScheduleId != id)
            throw new ScheduleLinkRefusal("wrong-schedule", "Сохраняйте связь из графика последователя.");

        var edges = await (from link in db.ScheduleLinks.AsNoTracking()
            join work in db.ScheduleWorks on link.SuccessorId equals work.Id
            join owner in db.Schedules on work.ScheduleId equals owner.Id
            where owner.ConstructionId == schedule.ConstructionId && (linkId == null || link.Id != linkId)
            orderby link.PredecessorId, link.SuccessorId
            select new ScheduleEdge(link.PredecessorId, link.SuccessorId)).ToListAsync(ct);
        if (edges.Contains(new ScheduleEdge(input.PredecessorId, input.SuccessorId)))
            throw new ConflictException("Эти работы уже связаны.");
        var cycle = CycleCheck.Find(edges, input.PredecessorId, input.SuccessorId);
        if (cycle.Count > 0)
            throw new ScheduleLinkRefusal("schedule-cycle", "Связь замыкает цикл работ.", await NameCycleAsync(cycle, ct));

        var linkToSave = existing ?? ScheduleLink.Create(input.PredecessorId, input.SuccessorId, input.Kind, input.Lag);
        var oldPredecessor = linkToSave.PredecessorId;
        if (existing is null) db.ScheduleLinks.Add(linkToSave);
        else linkToSave.Change(input.PredecessorId, input.SuccessorId, input.Kind, input.Lag);
        await TouchSchedulesAsync(schedule, oldPredecessor, input.PredecessorId, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return View(linkToSave, db.VersionOf(linkToSave));
    }

    internal async Task DeleteLinkAsync(Guid id, Guid linkId, HttpRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var schedule = await schedules.LockAsync(id, ct);
        var link = await OwnedAsync(id, linkId, request, ct);
        db.ScheduleLinks.Remove(link);
        await TouchSchedulesAsync(schedule, link.PredecessorId, link.PredecessorId, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<ScheduleLink> OwnedAsync(Guid id, Guid linkId, HttpRequest request, CancellationToken ct)
    {
        var link = await (from candidate in db.ScheduleLinks
            join work in db.ScheduleWorks on candidate.SuccessorId equals work.Id
            where candidate.Id == linkId && work.ScheduleId == id
            select candidate).SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Связь работ не найдена.");
        EstimateEndpoints.EnsureSeen(request, db.VersionOf(link), "Связь", "график");
        return link;
    }

    internal static async Task<IReadOnlyList<ScheduleRemovedLink>> RemovedWithWorksAsync(
        PlanDbContext db, Guid[] workIds, CancellationToken ct)
    {
        if (workIds.Length == 0) return [];
        return await (from link in db.ScheduleLinks.AsNoTracking()
            where workIds.Contains(link.PredecessorId) || workIds.Contains(link.SuccessorId)
            join predecessor in db.ScheduleWorks on link.PredecessorId equals predecessor.Id
            join predecessorSchedule in db.Schedules on predecessor.ScheduleId equals predecessorSchedule.Id
            join successor in db.ScheduleWorks on link.SuccessorId equals successor.Id
            join successorSchedule in db.Schedules on successor.ScheduleId equals successorSchedule.Id
            orderby link.Id
            select new ScheduleRemovedLink(link.Id,
                new ScheduleWorkRef(predecessor.Id, predecessor.Name, predecessorSchedule.Id, predecessorSchedule.Name, predecessor.SectionId),
                new ScheduleWorkRef(successor.Id, successor.Name, successorSchedule.Id, successorSchedule.Name, successor.SectionId),
                link.Kind, link.Lag)).ToListAsync(ct);
    }

    private async Task<List<ScheduleWorkRef>> NameCycleAsync(IReadOnlyList<Guid> cycle, CancellationToken ct)
    {
        var works = await (from work in db.ScheduleWorks
            join schedule in db.Schedules on work.ScheduleId equals schedule.Id
            where cycle.Contains(work.Id)
            select new ScheduleWorkRef(work.Id, work.Name, schedule.Id, schedule.Name, work.SectionId)).ToDictionaryAsync(w => w.Id, ct);
        return cycle.Select(id => works[id]).ToList();
    }

    private async Task TouchSchedulesAsync(Schedule owner, Guid oldPredecessor, Guid newPredecessor, CancellationToken ct)
    {
        owner.Touch();
        var neighbours = await db.Schedules.Where(s => s.Id != owner.Id && db.ScheduleWorks.Any(w =>
            w.ScheduleId == s.Id && (w.Id == oldPredecessor || w.Id == newPredecessor))).ToListAsync(ct);
        foreach (var neighbour in neighbours) neighbour.Touch();
    }

    internal static ScheduleLinkDto View(ScheduleLink link, string version) =>
        new(link.Id, link.PredecessorId, link.SuccessorId, link.Kind, link.Lag, version);
}
