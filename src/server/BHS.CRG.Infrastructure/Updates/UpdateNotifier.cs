using BHS.CRG.Application.Updates;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Updates;

/// <summary>
/// Кому и как сообщать о вышедшей версии (issue #813).
///
/// Отдельно от службы, потому что здесь единственное место, где принимается решение об адресате, —
/// и его нужно проверять тестом на живой базе, а не через фоновый цикл с походом в GitHub.
/// </summary>
public class UpdateNotifier(AppDbContext db, INotificationService notifier)
{
    public const string Source = "Обновления";

    /// <summary>
    /// Сообщаем тем, кто обслуживает систему, — одной записью с аудиторией
    /// <c>core.system.manage</c> (ТЗ AUTH-13, issue #949). Остальным сообщение «доступна версия»
    /// адресовано быть не может: обновляет систему не они. Версия при этом видна всем пассивно —
    /// её показывает подвал боковой панели, и никого не дёргает.
    ///
    /// ⚠️ Раньше здесь был перебор пользователей роли <c>Admin</c> с личной копией каждому. Так
    /// пришлось делать, пока «прочитано» лежало на самой записи и первый прочитавший снимал её у
    /// всех (issue #821); состояние давно у каждого своё, а перебор остался — вместе с двумя его
    /// свойствами. Первое: решение принималось по ИМЕНИ РОЛИ, чего права как раз и отменяют.
    /// Второе: получатели замерзали в момент выпуска — администратор, заведённый назавтра, не
    /// узнавал ничего, потому что «для системы уже отправлено». Отбор при чтении снимает оба.
    /// </summary>
    public async Task NotifyAsync(string latest, string installed, CancellationToken ct)
    {
        // Сообщения о ПРЕЖНИХ версиях убираем: к третьему выпуску в колокольчике лежали бы три
        // записи об одном и том же, и свежая терялась бы среди устаревших.
        await db.Notifications.Where(n => n.Source == Source).ExecuteDeleteAsync(ct);

        await notifier.PublishAsync(NotificationSeverity.Info,
            $"Доступна версия {latest}",
            $"Установлена {installed}. Обновление выполняется вручную — см. инструкцию по развёртыванию.",
            Source, audience: NotificationAudiences.SystemManage, ct: ct);
    }

    /// <summary>
    /// Снять сообщения об обновлении: система уже на свежей версии (issue #813).
    ///
    /// Нужно потому, что иначе запись живёт до СЛЕДУЮЩЕГО выпуска: обновились до 0.139.0 — а в
    /// колокольчике по-прежнему «Доступна версия 0.139.0. Установлена 0.138.0», и висеть это будет,
    /// пока не выйдет 0.140.0. Сообщение о выполненной работе — тот же мусор, что и лампа, горящая
    /// всегда, только с виду осмысленный.
    /// </summary>
    public async Task ClearAsync(CancellationToken ct)
        => await db.Notifications.Where(n => n.Source == Source).ExecuteDeleteAsync(ct);

    /// <summary>
    /// Сообщать ли о выпущенной версии. Чистое решение: «новее установленной» И «об этой ещё не
    /// сообщали». Второе условие хранится в базе, а не в памяти процесса, — «версия вышла» это факт,
    /// а не текущее состояние, и перезапуск api не повод повторять (при обновлении перезапуск
    /// происходит по определению).
    /// </summary>
    public static bool ShouldNotify(string? latest, string installed, string? alreadyNotified)
        => AppVersion.IsNewer(latest, installed)
           && !string.Equals(alreadyNotified, latest, StringComparison.OrdinalIgnoreCase);
}
