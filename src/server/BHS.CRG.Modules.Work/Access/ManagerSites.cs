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
    ///
    /// <para><b>Действует — пока сотрудник работает.</b> Годность проверяют заведение и правка, но увольнение
    /// и архив ставят позже, назначения не трогая: бессрочный «менеджер» иначе назначал бы и видел стройку и
    /// после ухода. Архивный — ничего; на день, когда сотрудник не работает (уволен с … или ещё не принят), —
    /// ничего.</para>
    /// </summary>
    public async Task<IReadOnlySet<Guid>> OfEmployeeAsync(ModuleEmployee employee, DateOnly? on = null, CancellationToken ct = default)
    {
        var sites = new HashSet<Guid>();
        if (employee.Archived) return sites;
        var managed = await db.Assignments.AsNoTracking()
            .Where(a => a.EmployeeId == employee.Id && a.Kind == AssignmentKind.Manager)
            .ToListAsync(ct);
        foreach (var assignment in managed)
        {
            if (sites.Contains(assignment.ConstructionId)) continue;
            // «Сегодня» спрашивается и при названной дате: незнакомая стройка отвечает null — так удалённая
            // стройка не остаётся «своей».
            if (await clock.TodayOnSiteAsync(assignment.ConstructionId, ct) is not { } today) continue;
            var day = on ?? today;
            if (assignment.Covers(day) && employee.NotWorkingOn(day) is null) sites.Add(assignment.ConstructionId);
        }
        return sites;
    }

    /// <summary>
    /// Учётные записи менеджеров каждой стройки (ТЗ WORK-56, <c>work-16</c>) — кому слать сводку о поданных: сотрудники с
    /// назначением «менеджер», действующим сегодня по поясу стройки, работающие и не в архиве, со связанной учётной
    /// записью. Стройка, которой больше нет, и стройка без такого менеджера в ответ не входят. Держатели
    /// <c>work.sites.all</c> без назначения — тоже: право не делает стройку своей.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ManagerAccountsAsync(
        IReadOnlyCollection<Guid> siteIds, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, IReadOnlyList<Guid>>();
        if (siteIds.Count == 0) return result;
        var wanted = siteIds.ToArray();
        var managed = await db.Assignments.AsNoTracking()
            .Where(a => wanted.Contains(a.ConstructionId) && a.Kind == AssignmentKind.Manager)
            .ToListAsync(ct);
        if (managed.Count == 0) return result;
        var people = (await employees.GetAsync([.. managed.Select(a => a.EmployeeId).Distinct()], ct) ?? [])
            .DistinctBy(e => e.Id).ToDictionary(e => e.Id);
        foreach (var site in managed.GroupBy(a => a.ConstructionId))
        {
            if (await clock.TodayOnSiteAsync(site.Key, ct) is not { } today) continue;
            var accounts = site
                .Where(a => a.Covers(today))
                .Select(a => people.GetValueOrDefault(a.EmployeeId))
                .Where(e => e is { Archived: false, Account: { IsLinked: true, UserId: not null } } && e.NotWorkingOn(today) is null)
                .Select(e => e!.Account.UserId!.Value)
                .Distinct().ToList();
            if (accounts.Count > 0) result[site.Key] = accounts;
        }
        return result;
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
        return ModuleReach.OfSites(await OfEmployeeAsync(me, ct: ct));
    }
}
