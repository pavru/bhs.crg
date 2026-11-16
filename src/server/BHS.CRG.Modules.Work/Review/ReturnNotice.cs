using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Work.Review;

/// <summary>
/// Личное уведомление о возврате отчёта (ТЗ WORK-13, WORK-56, D16 зонтика; change <c>work-16-review</c>) — учётной
/// записи сотрудника-исполнителя, а не аудитории правом: право разослало бы имя монтажника и стройку менеджерам всех
/// строек.
///
/// <para><b>Ссылки нет</b>: экрана отчёта ещё нет (<c>work-21</c>, <c>work-22</c>), и ссылка вела бы в пустоту. Её
/// дописывают те change вместе с экраном.</para>
///
/// <para>Публикуется после фиксации возврата, без токена отмены: возврат состоялся, и сбой колокольчика его не
/// отменяет — он пишется в журнал приложения.</para>
/// </summary>
public sealed class ReturnNotice(IModuleNotifications notifications, ILogger<ReturnNotice> logger)
{
    public async Task PublishAsync(Guid userId, Report report, string site, string comment, string manager)
    {
        try
        {
            var shift = report.Shift == ReportShift.Night ? "ночь" : "день";
            await notifications.PublishAsync(ModuleNotificationLevel.Warning,
                $"Отчёт за {report.Date:dd.MM} возвращён",
                $"{site}, {shift}. {manager}: {comment}",
                userId: userId, ct: CancellationToken.None);
        }
        catch (Exception lost)
        {
            logger.LogWarning(lost, "Отчёт {ReportId} возвращён, а уведомление монтажнику не ушло.", report.Id);
        }
    }
}
