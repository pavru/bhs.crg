using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Access;

/// <summary>
/// «Свои» стройки менеджера — стройки, где у его сотрудника действует назначение «менеджер» (ТЗ WORK-46,
/// WORK-39, WORK-40; change <c>work-11-assignments</c>), и охват читателя (<see cref="ModuleReach" />,
/// <c>work-07</c>), который из них следует.
///
/// <para><b>Действует — на сегодня по поясу КАЖДОЙ стройки</b> (ТЗ WORK-8): назначение, кончившееся вчера во
/// Владивостоке, уже не действует там, хотя в Москве вчера ещё не кончилось.</para>
///
/// <para><b>Кэша нет.</b> Снятое назначение обязано убрать стройку со следующего же запроса, без перевхода;
/// строек — десятки, назначений менеджера у человека — единицы, и чтение их дешевле любого сброса кэша.</para>
///
/// <para><b>Пустой набор — «ничего», а не «все»</b> (<see cref="ModuleReach.OfSites" />): менеджер, у которого
/// кончились назначения, не получает доступа ко всем стройкам.</para>
/// </summary>
public sealed class ManagerSites(WorkDbContext db, IModuleClock clock, IModuleEmployees employees)
{
    /// <summary>
    /// Стройки, где у сотрудника действует назначение «менеджер» — на <paramref name="on" />, а не названа
    /// дата — на сегодня каждой стройки. Стройка, которой больше нет, в набор не входит.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> OfEmployeeAsync(Guid employeeId, DateOnly? on = null, CancellationToken ct = default)
    {
        var managed = await db.Assignments.AsNoTracking()
            .Where(a => a.EmployeeId == employeeId && a.Kind == AssignmentKind.Manager)
            .ToListAsync(ct);
        var sites = new HashSet<Guid>();
        foreach (var assignment in managed)
        {
            if (sites.Contains(assignment.ConstructionId)) continue;
            // «Сегодня» спрашивается и при названной дате: незнакомая стройка отвечает null — так удалённая
            // стройка не остаётся «своей».
            if (await clock.TodayOnSiteAsync(assignment.ConstructionId, ct) is not { } today) continue;
            if (assignment.Covers(on ?? today)) sites.Add(assignment.ConstructionId);
        }
        return sites;
    }

    /// <summary>
    /// Охват читателя как менеджера: право «на любых стройках» — все; иначе стройки его действующих
    /// назначений «менеджер». Система и учётная запись без сотрудника — «ничего»: своих строек у них нет,
    /// а угадывать за них — значит открыть лишнее.
    /// </summary>
    public async Task<ModuleReach> ReachAsync(ModuleReader reader, CancellationToken ct = default)
    {
        if (reader.IsSystem) return ModuleReach.OfSites([]);
        if (reader.Allows(WorkModule.SitesAll)) return ModuleReach.All;
        if (await employees.OfAccountAsync(reader.UserId!.Value, ct) is not { } me) return ModuleReach.OfSites([]);
        return ModuleReach.OfSites(await OfEmployeeAsync(me.Id, ct: ct));
    }
}
