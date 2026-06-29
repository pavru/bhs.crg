using BHS.CRG.Domain.Notifications;

namespace BHS.CRG.Application.Notifications;

public record NotificationDto(
    Guid Id,
    NotificationSeverity Severity,
    string Title,
    string Message,
    string? Source,
    string? LinkUrl,
    string? LinkLabel,
    bool IsRead,
    DateTimeOffset CreatedAt);

/// <summary>
/// Подсистема уведомлений: публикация событий (длительные операции, переходы состояния)
/// и управление списком. Видимость: пользователь видит свои (userId) + общесистемные (userId == null).
/// </summary>
public interface INotificationService
{
    Task PublishAsync(NotificationSeverity severity, string title, string message,
        string? source = null, Guid? userId = null, string? linkUrl = null, string? linkLabel = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly = false, int take = 100, CancellationToken ct = default);
    Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default);
    Task MarkReadAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task MarkAllReadAsync(Guid userId, CancellationToken ct = default);
    Task DismissAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task ClearAsync(Guid userId, CancellationToken ct = default);
}
