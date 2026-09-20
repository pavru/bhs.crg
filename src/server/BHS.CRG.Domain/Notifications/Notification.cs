using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Notifications;

/// <summary>Тип уведомления: Информация, Предупреждение, Ошибка.</summary>
public enum NotificationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// Запись подсистемы уведомлений: события длительных операций (распознавание, генерация)
/// и переходы состояния системы/внешних компонент (health-мониторинг).
/// </summary>
public class Notification : Entity
{
    public NotificationSeverity Severity { get; private set; }
    public string Title { get; private set; } = "";
    public string Message { get; private set; } = "";

    /// <summary>Источник/категория: «Генерация», «Распознавание», «Состояние системы» и т.п.</summary>
    public string? Source { get; private set; }

    /// <summary>
    /// Владелец уведомления. null = общесистемное (видно всем, напр. состояние компонент);
    /// заданный id = личное уведомление пользователя (результат его long-running job).
    /// </summary>
    public Guid? UserId { get; private set; }

    /// <summary>
    /// Кому адресовано общесистемное уведомление (ТЗ AUTH-13, CORE-27): код права
    /// (<c>core.system.manage</c>) или код модуля (<c>id</c> — любое его право). null = всем вошедшим.
    ///
    /// Одно поле на оба случая потому, что получатель сверяется НАБОРОМ ключей: его права плюс коды
    /// модулей, к которым у него есть доступ. Разбирать строку при чтении не нужно, и потому нельзя
    /// разобрать её по-разному в двух местах.
    ///
    /// ⚠️ Осмысленно только у общесистемного (<see cref="UserId" /> == null). У личного адресат уже
    /// назван, и право его не сузит: своё уведомление человек видит потому, что оно его.
    /// </summary>
    public string? Audience { get; private set; }

    /// <summary>Ссылка на результат (напр. путь скачивания сгенерированного файла) — для прямого доступа.</summary>
    public string? LinkUrl { get; private set; }
    public string? LinkLabel { get; private set; }

    // «Прочитано»/«скрыто» здесь НЕТ намеренно: это состояние пары (уведомление, пользователь),
    // а не самого уведомления — см. NotificationUserState и issue #821.

    private Notification() { }

    public static Notification Create(NotificationSeverity severity, string title, string message,
        string? source, Guid? userId = null, string? linkUrl = null, string? linkLabel = null,
        string? audience = null)
    {
        // Личное И по праву разом — путаница в издателе: адресат уже назван, и право его не сузит.
        // Молча обнулить одно из двух значило бы отправить не туда, куда просили, и промолчать.
        if (userId is not null && audience is not null)
            throw new ArgumentException(
                $"Уведомление «{title}» адресовано и лично, и по праву «{audience}» — выберите одно.",
                nameof(audience));

        return new()
        {
            Severity = severity,
            Title = title,
            Message = message,
            Source = source,
            UserId = userId,
            LinkUrl = linkUrl,
            LinkLabel = linkLabel,
            Audience = audience,
        };
    }
}
