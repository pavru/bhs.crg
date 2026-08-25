using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Notifications;

/// <summary>
/// Состояние уведомления у КОНКРЕТНОГО пользователя: прочитано / скрыто.
///
/// Появилось потому, что раньше признак «прочитано» лежал на самой записи уведомления, а
/// общесистемное уведомление (<see cref="Notification.UserId"/> == null) видно всем: один
/// пользователь отмечал прочитанным — гасло у всех, один смахивал крестиком — исчезало у всех,
/// «Очистить все» стирало уведомления всей компании (issue #821).
///
/// Строки заводятся лениво: нет строки — уведомление не прочитано и не скрыто. Поэтому список
/// читается левым соединением, а не внутренним.
/// </summary>
public class NotificationUserState : Entity
{
    public Guid NotificationId { get; private set; }
    public Guid UserId { get; private set; }

    public bool IsRead { get; private set; }

    /// <summary>Скрыто пользователем (крестик / «Очистить все»). Само уведомление остаётся у остальных.</summary>
    public bool IsDismissed { get; private set; }

    private NotificationUserState() { }

    public static NotificationUserState Create(Guid notificationId, Guid userId, bool isRead = false, bool isDismissed = false)
        => new()
        {
            NotificationId = notificationId,
            UserId = userId,
            IsRead = isRead,
            IsDismissed = isDismissed,
        };
}
