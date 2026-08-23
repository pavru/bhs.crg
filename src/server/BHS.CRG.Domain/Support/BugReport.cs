using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Support;

/// <summary>Что стало с сообщением об ошибке после того, как его прочитал администратор.</summary>
public enum BugReportStatus
{
    /// <summary>Пришло от пользователя, администратор ещё не разбирал.</summary>
    New = 0,

    /// <summary>Передано разработчикам: заведён issue, номер и адрес — в <see cref="BugReport.GithubIssueNumber" />.</summary>
    Forwarded = 1,

    /// <summary>Исправлено в версии <see cref="BugReport.FixedInVersion" />.</summary>
    Fixed = 2,

    /// <summary>Не баг (вопрос, ожидаемое поведение, повтор) — работы не будет.</summary>
    Rejected = 3,
}

/// <summary>
/// Сообщение об ошибке, отправленное пользователем из приложения (issue #834).
///
/// Копится ВНУТРИ системы, а не уезжает в GitHub напрямую. Репозиторий публичный, а пользователь
/// описывает беду теми словами, что у него есть, — названиями строек, организаций и объектов. Между
/// автором и интернетом обязан стоять читатель, и в модели ролей это место занято администратором:
/// он убирает внутреннее и одной кнопкой создаёт issue из <see cref="IssueDraft" />.
///
/// Снимок экрана в GitHub не уходит НИКОГДА (<see cref="ScreenshotBlobPath" /> остаётся здесь):
/// через API его к issue и не приложить, а на экране пользователя видно ровно то, чего мы наружу
/// не отдаём.
/// </summary>
public class BugReport : Entity
{
    /// <summary>Кто отправил. По нему же уходит личное уведомление о смене статуса.</summary>
    public Guid AuthorId { get; private set; }

    /// <summary>«Что произошло» словами автора — единственное, что он обязан заполнить.</summary>
    public string Message { get; private set; } = "";

    /// <summary>
    /// Технический контекст (JSON): версия и сборка клиента, маршрут, браузер, размер окна,
    /// последние ошибки API (метод, путь, статус, идентификатор запроса) и стек, если сообщение
    /// пришло с экрана сбоя. Тел ответов и содержимого форм здесь нет намеренно.
    /// </summary>
    public string? TechContext { get; private set; }

    /// <summary>Снимок экрана в хранилище. Виден только администратору.</summary>
    public string? ScreenshotBlobPath { get; private set; }

    public BugReportStatus Status { get; private set; }

    /// <summary>
    /// Текст будущего issue, отредактированный администратором. Пока <c>null</c> — не редактировали,
    /// и показывать надо заготовку, собранную из сообщения и техблока. Хранить заготовку в базе
    /// незачем: она вычислима, а сохранённая копия разошлась бы с сообщением при первой же правке
    /// формы заготовки.
    /// </summary>
    public string? IssueDraft { get; private set; }

    public int? GithubIssueNumber { get; private set; }
    public string? GithubIssueUrl { get; private set; }

    /// <summary>Версия, в которой исправлено, — её называет администратор.</summary>
    public string? FixedInVersion { get; private set; }

    private BugReport() { }

    public static BugReport Create(Guid authorId, string message, string? techContext, string? screenshotBlobPath)
        => new()
        {
            AuthorId = authorId,
            Message = message,
            TechContext = techContext,
            ScreenshotBlobPath = screenshotBlobPath,
            Status = BugReportStatus.New,
        };

    public void SaveDraft(string? text)
    {
        IssueDraft = string.IsNullOrWhiteSpace(text) ? null : text;
        TouchUpdatedAt();
    }

    /// <summary>Передано разработчикам: issue заведён.</summary>
    public void MarkForwarded(int issueNumber, string issueUrl)
    {
        GithubIssueNumber = issueNumber;
        GithubIssueUrl = issueUrl;
        Status = BugReportStatus.Forwarded;
        TouchUpdatedAt();
    }

    /// <summary>
    /// Исправлено. Номер issue не стираем, если он был: «исправлено» приходит ПОСЛЕ передачи, и
    /// автору полезно видеть ту же ссылку, что он получил раньше.
    /// </summary>
    public void MarkFixed(string version)
    {
        FixedInVersion = version;
        Status = BugReportStatus.Fixed;
        TouchUpdatedAt();
    }

    public void Reject()
    {
        Status = BugReportStatus.Rejected;
        TouchUpdatedAt();
    }

    /// <summary>Вернуть в разбор: администратор ошибся статусом.</summary>
    public void Reopen()
    {
        Status = GithubIssueNumber is null ? BugReportStatus.New : BugReportStatus.Forwarded;
        FixedInVersion = null;
        TouchUpdatedAt();
    }
}
