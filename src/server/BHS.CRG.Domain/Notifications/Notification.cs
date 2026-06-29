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

    /// <summary>Ссылка на результат (напр. путь скачивания сгенерированного файла) — для прямого доступа.</summary>
    public string? LinkUrl { get; private set; }
    public string? LinkLabel { get; private set; }

    public bool IsRead { get; private set; }

    private Notification() { }

    public static Notification Create(NotificationSeverity severity, string title, string message,
        string? source, Guid? userId = null, string? linkUrl = null, string? linkLabel = null)
        => new()
        {
            Severity = severity,
            Title = title,
            Message = message,
            Source = source,
            UserId = userId,
            LinkUrl = linkUrl,
            LinkLabel = linkLabel,
        };

    public void MarkRead()
    {
        if (IsRead) return;
        IsRead = true;
        TouchUpdatedAt();
    }
}
