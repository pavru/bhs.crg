using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Schedules;

internal sealed class ScheduleWorkEditor(
    PlanDbContext db, ScheduleBuilder schedules, ScheduleViews views,
    IModulePlanItems items, IModuleWorkTypes types, IModuleEmployees employees)
{
    internal async Task<ScheduleWorkDto> AddAsync(Guid id, ScheduleManualWork input, CancellationToken ct)
    {
        if (input.Work is null) throw new InvalidRequestException("Заполните поля работы.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var schedule = await schedules.LockAsync(id, ct);
        var workType = (await types.GetAsync([input.WorkTypeId], ct))?.SingleOrDefault()
            ?? throw new InvalidRequestException("Вид работы не найден в классификаторе.");
        if (workType.UnitId is not { } unit)
            throw new InvalidRequestException("Единица вида работы не задана в классификаторе.");
        var key = new PlanItemKey(workType.Id, schedule.ConstructionId, input.SectionId, unit);
        var employeeIds = await CheckAssigneesAsync(input.Work.EmployeeIds, [], ct);
        var position = await items.FindOrCreateAsync(key, ct);
        if (!position.Found) throw new InvalidRequestException(position.Refusal.Reason);

        var work = ScheduleWork.Create(id, position.Item.Id, null, input.SectionId, workType.DisplayName, input.Work.Volume);
        work.Edit(input.Work.Volume, input.Work.Start, input.Work.End);
        db.ScheduleWorks.Add(work);
        db.ScheduleWorkAssignees.AddRange(employeeIds.Select(e => new ScheduleWorkAssignee(work.Id, e)));
        schedule.Touch();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return views.Work(work, key, employeeIds);
    }

    internal async Task<ScheduleWorkDto> EditAsync(
        Guid id, Guid workId, ScheduleWorkInput input, HttpRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var schedule = await schedules.LockAsync(id, ct);
        var work = await LoadAsync(id, workId, request, ct);
        var existing = await db.ScheduleWorkAssignees.Where(a => a.WorkId == workId).ToListAsync(ct);
        var employeeIds = await CheckAssigneesAsync(input.EmployeeIds, existing.Select(a => a.EmployeeId).ToHashSet(), ct);
        var position = (await items.GetAsync([work.PlanItemId], ct)).Single();
        work.Edit(input.Volume, input.Start, input.End);
        db.ScheduleWorkAssignees.RemoveRange(existing.Where(a => !employeeIds.Contains(a.EmployeeId)));
        var previous = existing.Select(a => a.EmployeeId).ToHashSet();
        db.ScheduleWorkAssignees.AddRange(employeeIds.Where(e => !previous.Contains(e))
            .Select(e => new ScheduleWorkAssignee(workId, e)));
        schedule.Touch();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return views.Work(work, position.Key, employeeIds);
    }

    internal async Task DeleteWorkAsync(Guid id, Guid workId, HttpRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var schedule = await schedules.LockAsync(id, ct);
        db.ScheduleWorks.Remove(await LoadAsync(id, workId, request, ct));
        schedule.Touch();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<ScheduleWork> LoadAsync(Guid id, Guid workId, HttpRequest request, CancellationToken ct)
    {
        var work = await db.ScheduleWorks.SingleOrDefaultAsync(w => w.ScheduleId == id && w.Id == workId, ct)
            ?? throw new NotFoundException("Работа графика не найдена.");
        EstimateEndpoints.EnsureSeen(request, db.VersionOf(work), "Работу", "график");
        return work;
    }

    private async Task<Guid[]> CheckAssigneesAsync(
        IReadOnlyList<Guid>? requested, HashSet<Guid> existing, CancellationToken ct)
    {
        var wanted = (requested ?? []).Distinct().Order().ToArray();
        var added = wanted.Where(e => !existing.Contains(e)).ToArray();
        if (added.Length == 0) return wanted;

        var known = (await employees.GetAsync(added, ct) ?? throw new InvalidRequestException(IModuleEmployees.NotSetUp))
            .ToDictionary(e => e.Id);
        foreach (var id in added)
        {
            if (!known.TryGetValue(id, out var employee))
                throw new InvalidRequestException($"Сотрудник {id} не найден в справочнике.");
            if (employee.Archived)
                throw new InvalidRequestException($"«{employee.FullName}»: запись в архиве.");
        }
        return wanted;
    }
}
