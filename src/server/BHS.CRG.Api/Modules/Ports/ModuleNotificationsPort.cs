using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Уведомления модуля — в колокольчик ядра (ТЗ AUTH-13, CORE-27).
///
/// <para>Проверку объявленной аудитории делает сама служба уведомлений, и переспрашивать её здесь не
/// нужно: у неё и каталог прав, и реестр модулей.</para>
///
/// <para>Поле «источник» модулю не выведено: ядро показывает его человеку как подпись издателя, а
/// модуль называет себя в заголовке уведомления. Два места с одной и той же подписью разошлись бы
/// молча — и в колокольчике оказалось бы «Счета» рядом с заголовком от другого раздела.</para>
/// </summary>
public sealed class ModuleNotificationsPort(INotificationService notifications) : IModuleNotifications
{
    public Task<Guid> PublishAsync(ModuleNotificationLevel level, string title, string message,
        string? audience = null, Guid? userId = null, string? linkUrl = null, string? linkLabel = null,
        CancellationToken ct = default)
    {
        // Адресат назван дважды — отказ, а не выбор одного из двух. «Право и пользователь» означает
        // разные списки получателей, и молчаливое предпочтение одного из них выглядело бы отправкой
        // тем, кого никто не называл.
        if (audience is not null && userId is not null)
            throw new InvalidOperationException(
                "У уведомления названы и аудитория, и получатель. Это разные способы назвать " +
                "адресата: аудитория — право или код модуля (видят все, у кого оно есть), " +
                "получатель — один человек. Выберите один.");

        return notifications.PublishAsync(Severity(level), title, message,
            source: null, userId: userId, linkUrl: linkUrl, linkLabel: linkLabel, audience: audience, ct: ct);
    }

    /// <summary>
    /// Зеркало в оригинал. Перечислением, а не приведением номера к номеру: значения совпадают
    /// сегодня, и приведение молча съехало бы при первом же новом значении в любом из двух
    /// перечислений. Состав зеркала сверяет <c>ModulePortMirrorTests</c>.
    /// </summary>
    private static NotificationSeverity Severity(ModuleNotificationLevel level) => level switch
    {
        ModuleNotificationLevel.Info => NotificationSeverity.Info,
        ModuleNotificationLevel.Warning => NotificationSeverity.Warning,
        ModuleNotificationLevel.Error => NotificationSeverity.Error,
        _ => throw new InvalidOperationException($"Неизвестная важность уведомления модуля: {level}."),
    };
}
