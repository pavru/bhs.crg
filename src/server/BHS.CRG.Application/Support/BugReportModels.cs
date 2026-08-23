using System.Text.Json;
using BHS.CRG.Domain.Support;

namespace BHS.CRG.Application.Support;

/// <summary>Строка списка сообщений: всё, что нужно, чтобы выбрать следующее для разбора.</summary>
public record BugReportListItem(
    Guid Id,
    string Author,
    BugReportStatus Status,
    /// <summary>Первая строка сообщения — в списке текст целиком не нужен.</summary>
    string Summary,
    int? GithubIssueNumber,
    string? FixedInVersion,
    bool HasScreenshot,
    DateTimeOffset CreatedAt);

/// <summary>Сообщение целиком — то, что видит администратор в правой панели.</summary>
public record BugReportDetail(
    Guid Id,
    string Author,
    string? AuthorEmail,
    string Message,
    /// <summary>Техблок как его прислал клиент; <c>null</c>, если не прислал или он не разобрался.</summary>
    JsonElement? Tech,
    string? ScreenshotBlobPath,
    BugReportStatus Status,
    /// <summary>Текст будущего issue: правка администратора либо собранная заготовка.</summary>
    string IssueDraft,
    /// <summary>Заготовку уже правили — значит перезаписывать её при перезагрузке нельзя.</summary>
    bool DraftEdited,
    int? GithubIssueNumber,
    string? GithubIssueUrl,
    string? FixedInVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Сообщения об ошибках из приложения (issue #834): приём от пользователя и разбор администратором.
///
/// Отправлять может любой вошедший, читать и менять — только администратор. Разделение не про
/// секретность самих сообщений, а про то, что между автором и публичным репозиторием обязан стоять
/// читатель: пользователь описывает беду названиями строек и организаций.
/// </summary>
public interface IBugReportService
{
    /// <summary>Принять сообщение и уведомить администраторов. Возвращает идентификатор записи.</summary>
    Task<Guid> SubmitAsync(Guid authorId, string message, JsonElement? tech,
        string? screenshotBlobPath, CancellationToken ct = default);

    Task<IReadOnlyList<BugReportListItem>> ListAsync(CancellationToken ct = default);
    Task<BugReportDetail> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Сохранить правку текста issue. Пустой текст возвращает запись к заготовке.</summary>
    Task<BugReportDetail> SaveDraftAsync(Guid id, string? text, CancellationToken ct = default);

    Task<BugReportDetail> MarkFixedAsync(Guid id, string version, CancellationToken ct = default);
    Task<BugReportDetail> RejectAsync(Guid id, CancellationToken ct = default);

    /// <summary>Вернуть в разбор — администратор ошибся статусом.</summary>
    Task<BugReportDetail> ReopenAsync(Guid id, CancellationToken ct = default);
}
