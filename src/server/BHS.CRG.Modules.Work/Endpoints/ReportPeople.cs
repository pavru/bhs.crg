using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>
/// Кто действует и над чьим отчётом (ТЗ WORK-31) — общее у пути записи отчёта (<see cref="ReportDesk" />) и приёмки
/// (<c>work-16</c>): учётная запись запроса с её сотрудником и сотрудник отчёта с именем снимком.
/// </summary>
public sealed class ReportPeople(IModuleUser user, IModuleEmployees employees)
{
    /// <summary>Учётная запись запроса — для «ввёл и принял один человек» и уведомлений.</summary>
    public Guid? UserId => user.Id;

    /// <summary>
    /// Кто действует: учётная запись запроса и её сотрудник — у «своих» он известен охвату, у ввода за сотрудника
    /// спрашивается по связи учётной записи (ТЗ WORK-31: «вводит Иванов за Петрова»).
    /// </summary>
    public async Task<ReportJournalActor> ActorAsync(ReportViewer viewer, CancellationToken ct)
    {
        var employee = viewer.EmployeeId ?? (user.Id is { } id ? (await employees.OfAccountAsync(id, ct))?.Id : null);
        if (viewer.FieldDelivery is not null && employee is { } fieldEmployee)
            return new ReportJournalActor(null, await ExecutorAsync(fieldEmployee, ct), fieldEmployee);
        return new ReportJournalActor(user.Id, user.Name, employee);
    }

    /// <summary>Сотрудник отчёта; справочника нет или сотрудник не найден — <c>null</c>.</summary>
    public async Task<ModuleEmployee?> EmployeeAsync(Guid employeeId, CancellationToken ct) =>
        (await employees.GetAsync([employeeId], ct))?.FirstOrDefault();

    /// <summary>Имя исполнителя снимком (через связь <c>work-01</c>); справочника нет — идентификатор.</summary>
    public async Task<string> ExecutorAsync(Guid employeeId, CancellationToken ct) =>
        (await EmployeeAsync(employeeId, ct))?.FullName ?? employeeId.ToString();
}

/// <summary>
/// Дубль действий над отчётом в журнал ядра (ТЗ CORE-28) — после фиксации, без токена отмены и без права на отказ:
/// изменение состоялось, и оборванный запрос или сбой журнала не вправе ответить «сбой» про него (как удаление счёта).
/// </summary>
public sealed class ReportActivity(IModuleConstructions constructions, IModuleActivityLog log, ILogger<ReportActivity> logger)
{
    /// <param name="site">Имя стройки, если вызывающий его уже знает; иначе — читается здесь.</param>
    public async Task RecordAsync(
        ModuleActivityAction action, Report report, string executor, string? before, string after, string? site = null)
    {
        try
        {
            site ??= await SiteNameAsync(report.ConstructionId);
            await log.RecordAsync(action, report.Id.ToString(), ReportActions.Label(executor, site, report),
                before, after, CancellationToken.None);
        }
        catch (Exception lost)
        {
            logger.LogWarning(lost, "Отчёт {ReportId}: действие {Action} записано в журнал отчёта, а в журнал действий не легло.",
                report.Id, action.Code);
        }
    }

    /// <summary>Имя стройки для подписи; стройки больше нет — «стройка».</summary>
    /// <remarks>Сбой справочника строек — тоже «стройка»: подпись не вправе уронить состоявшееся действие.</remarks>
    public async Task<string> SiteNameAsync(Guid siteId)
    {
        try
        {
            return (await constructions.ListAsync(CancellationToken.None)).FirstOrDefault(c => c.Id == siteId)?.Name ?? "стройка";
        }
        catch (Exception lost)
        {
            logger.LogWarning(lost, "Имя стройки {SiteId} не прочитано — в подписи «стройка».", siteId);
            return "стройка";
        }
    }
}
