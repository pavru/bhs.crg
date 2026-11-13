using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Access;

/// <summary>
/// Право по стройке (ТЗ WORK-21, WORK-46; change <c>work-11-assignments</c>): может ли спрашивающий назначать
/// на стройке S — и снять ли ему это назначение.
///
/// <para><b>Одно место ответа.</b> Его зовёт каждый пишущий адрес назначений — и из него же считаются признаки
/// <c>canAssign</c>, <c>canChange</c>, <c>canRemove</c> в ответе чтения (D15 зонтика). Кнопка и отказ поэтому
/// не расходятся: экран не вычисляет право сам, а показывает то, чем ответит запись.</para>
///
/// <para>Код права <c>work.assignment.edit</c> держат ворота адресов; здесь — то, чего воротами не выразить:
/// стройка. Право <c>work.sites.all</c> спрашивается у ядра (<see cref="IModuleUser.HasAsync" />), из базы, а
/// не из токена.</para>
///
/// <para>Ответ считается один раз на запрос (служба — scoped): чтение назначений стройки спрашивает признаки у
/// каждой строки, а сотрудник и стройки спрашивающего от строки не зависят.</para>
/// </summary>
public sealed class AssignmentGate(IModuleUser user, IModuleEmployees employees, ManagerSites sites, WorkDbContext db,
    IModuleClock clock)
{
    /// <summary>Учётная запись не связана с сотрудником: «свои» стройки считать не от кого.</summary>
    public const string NotLinked =
        "Ваша учётная запись не связана с сотрудником, поэтому своих строек у вас нет: назначает менеджер " +
        "стройки — сотрудник, связанный с учётной записью. Связь заводит администратор на экране «Сотрудники».";

    public const string NotManager =
        "Вы не менеджер этой стройки: назначать на ней может её менеджер или пользователь с правом «на любых стройках».";

    public const string LastManager =
        "На стройке не останется менеджера: последнее действующее назначение «менеджер» снимает или сокращает до " +
        "прошлого только пользователь с правом «на любых стройках».";

    private (bool All, IReadOnlySet<Guid> Managed, bool Linked)? _state;

    /// <summary>Есть ли у спрашивающего право «на любых стройках».</summary>
    public async Task<bool> SitesAllAsync(CancellationToken ct = default) => (await StateAsync(ct)).All;

    /// <summary>Почему спрашивающему нельзя назначать на стройке; <c>null</c> — можно.</summary>
    public async Task<string?> RefusalAsync(Guid constructionId, CancellationToken ct = default)
    {
        var state = await StateAsync(ct);
        if (state.All) return null;
        if (!state.Linked) return NotLinked;
        return state.Managed.Contains(constructionId) ? null : NotManager;
    }

    /// <summary>Отказ 403 с причиной, если назначать на стройке нельзя.</summary>
    public async Task EnsureAsync(Guid constructionId, CancellationToken ct = default)
    {
        if (await RefusalAsync(constructionId, ct) is { } refusal) throw new ForbiddenException(refusal);
    }

    /// <summary>
    /// Почему назначение нельзя снять: право по стройке или «на стройке не останется менеджера». <c>null</c> —
    /// можно. Второе — не запрет права, а состояние стройки: адрес отвечает на него 409, а не 403.
    /// </summary>
    public async Task<(string? Refusal, bool Forbidden)> RemovalAsync(Assignment assignment, CancellationToken ct = default)
    {
        if (await RefusalAsync(assignment.ConstructionId, ct) is { } refusal) return (refusal, true);
        return await LeavesNoManagerAsync(assignment, null, ct) ? (LastManager, false) : (null, false);
    }

    /// <summary>
    /// Оставит ли снятие (<paramref name="newPeriod" /> = <c>null</c>) или правка периода стройку без
    /// действующего на сегодня менеджера. С правом «на любых стройках» — не вопрос: ему и разрешено.
    /// </summary>
    public async Task<bool> LeavesNoManagerAsync(
        Assignment assignment, (DateOnly From, DateOnly? To)? newPeriod, CancellationToken ct = default)
    {
        if (assignment.Kind != AssignmentKind.Manager || await SitesAllAsync(ct)) return false;
        if (await clock.TodayOnSiteAsync(assignment.ConstructionId, ct) is not { } today) return false;
        if (!assignment.Covers(today)) return false;
        if (newPeriod is { } p && p.From <= today && (p.To is not { } to || today <= to)) return false;

        var others = await db.Assignments.AsNoTracking()
            .Where(a => a.ConstructionId == assignment.ConstructionId && a.Kind == AssignmentKind.Manager
                && a.Id != assignment.Id && a.DateFrom <= today && (a.DateTo == null || a.DateTo >= today))
            .AnyAsync(ct);
        return !others;
    }

    private async Task<(bool All, IReadOnlySet<Guid> Managed, bool Linked)> StateAsync(CancellationToken ct)
    {
        if (_state is { } known) return known;
        var all = await user.HasAsync(WorkModule.SitesAll, ct);
        var me = user.Id is { } id ? await employees.OfAccountAsync(id, ct) : null;
        IReadOnlySet<Guid> managed = me is null ? new HashSet<Guid>() : await sites.OfEmployeeAsync(me.Id, ct: ct);
        _state = (all, managed, me is not null);
        return _state.Value;
    }
}
