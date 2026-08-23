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

/// <summary>
/// Страница списка. Пара «что показываем» и «сколько всего» нужна ради честности: список отдаёт
/// только последние <see cref="IBugReportService.ListLimit" /> сообщений, и молчаливое усечение
/// читалось бы как «больше ничего нет».
/// </summary>
public record BugReportList(
    IReadOnlyList<BugReportListItem> Items,
    int Total,
    /// <summary>
    /// Задан ли токен GitHub. Нужен интерфейсу не для того, чтобы прятать кнопку, а чтобы она
    /// ОБЪЯСНЯЛА: спрятанная кнопка оставляет администратора гадать, куда делась передача.
    /// </summary>
    bool GithubConfigured);

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

    /// <summary>Сколько сообщений отдаёт список за раз. Постраничности нет — сказать о пределе есть.</summary>
    const int ListLimit = 500;

    Task<BugReportList> ListAsync(CancellationToken ct = default);
    Task<BugReportDetail> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Сохранить правку текста issue. Пустой текст возвращает запись к заготовке.</summary>
    Task<BugReportDetail> SaveDraftAsync(Guid id, string? text, CancellationToken ct = default);

    /// <summary>
    /// Завести issue в GitHub из отредактированного текста и отметить сообщение переданным.
    /// Заголовок пишет администратор: он один видел и текст, и то, что из него убрано.
    /// </summary>
    /// <param name="body">
    /// Текст issue С ЭКРАНА администратора. Пусто — берём сохранённую правку, а нет и её —
    /// заготовку.
    ///
    /// Приходит из запроса, а не читается из базы, потому что иначе публиковалось бы НЕ ТО, что
    /// человек видит: убрал он из текста названия строек, не нажал «Сохранить» — и наружу ушёл бы
    /// прежний, неотредактированный. Ровно от этого исхода вся конструкция и защищает; узнать о нём
    /// было бы неоткуда — на экране остался бы текст, который администратор считал отправленным.
    /// </param>
    Task<BugReportDetail> ForwardToGithubAsync(
        Guid id, string title, string? body = null, CancellationToken ct = default);

    /// <summary>Потолок заголовка issue. GitHub принимает и больше, но читать такое нельзя.</summary>
    const int TitleLimit = 200;

    Task<BugReportDetail> MarkFixedAsync(Guid id, string version, CancellationToken ct = default);
    Task<BugReportDetail> RejectAsync(Guid id, CancellationToken ct = default);

    /// <summary>Вернуть в разбор — администратор ошибся статусом.</summary>
    Task<BugReportDetail> ReopenAsync(Guid id, CancellationToken ct = default);
}
