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
/// и управление списком.
///
/// Видимость: личные (свой <c>userId</c>) плюс общесистемные, аудитория которых пользователю
/// подходит (ТЗ AUTH-13, CORE-27). Аудитория — код права или код модуля; без неё уведомление
/// приходит всем вошедшим, и это осознанный выбор издателя, а не значение по умолчанию «потому что
/// так вышло»: см. <see cref="NotificationAudiences" />.
/// </summary>
public interface INotificationService
{
    /// <param name="audience">
    /// Кому адресовано общесистемное уведомление: право или модуль (<see cref="NotificationAudiences" />).
    /// Вместе с <paramref name="userId" /> не задаётся — это разные способы назвать адресата.
    /// </param>
    /// <returns>
    /// Идентификатор созданной записи — чтобы издатель мог сразу скрыть её у того, кому она не
    /// нужна (автор собственного обращения), не заводя для этого поля в самой записи.
    /// </returns>
    Task<Guid> PublishAsync(NotificationSeverity severity, string title, string message,
        string? source = null, Guid? userId = null, string? linkUrl = null, string? linkLabel = null,
        string? audience = null, CancellationToken ct = default);

    Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly = false, int take = 100, CancellationToken ct = default);
    Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default);
    Task MarkReadAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task MarkAllReadAsync(Guid userId, CancellationToken ct = default);
    Task DismissAsync(Guid id, Guid userId, CancellationToken ct = default);
    Task ClearAsync(Guid userId, CancellationToken ct = default);
}
