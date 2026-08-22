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
    /// Сообщаем АДРЕСНО администраторам, а не общесистемно (<c>UserId == null</c>).
    ///
    /// Причина не в секретности — версия не тайна. У общесистемного уведомления состояние прочтения
    /// общее на всех: любой пользователь пометил прочитанным или смахнул крестиком, и записи не
    /// стало НИ У КОГО (<c>MarkReadAsync</c>/<c>DismissAsync</c>/<c>ClearAsync</c> работают
    /// <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> по строке). Тот единственный, кто может обновить
    /// систему, узнавал бы последним или никогда.
    ///
    /// Остальным версия доступна пассивно — её показывает подвал боковой панели: виден всегда, всем,
    /// и никого не дёргает.
    /// </summary>
    public async Task NotifyAsync(string latest, string installed, CancellationToken ct)
    {
        var adminIds = await db.UserRoles
            .Where(ur => db.Roles.Any(r => r.Id == ur.RoleId && r.Name == "Admin"))
            .Select(ur => ur.UserId)
            .Distinct()
            .ToListAsync(ct);
        if (adminIds.Count == 0) return;

        // Сообщения о ПРЕЖНИХ версиях убираем: к третьему выпуску в колокольчике лежали бы три
        // записи об одном и том же, и свежая терялась бы среди устаревших.
        await db.Notifications
            .Where(n => n.Source == Source && n.UserId != null && adminIds.Contains(n.UserId.Value))
            .ExecuteDeleteAsync(ct);

        foreach (var id in adminIds)
            await notifier.PublishAsync(NotificationSeverity.Info,
                $"Доступна версия {latest}",
                $"Установлена {installed}. Обновление выполняется вручную — см. инструкцию по развёртыванию.",
                Source, userId: id, ct: ct);
    }

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
