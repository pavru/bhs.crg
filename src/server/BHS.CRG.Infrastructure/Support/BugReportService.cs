using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Application.Support;
using BHS.CRG.Application.Updates;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Domain.Support;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Support;

/// <summary>
/// Приём сообщений об ошибках и их разбор администратором (issue #834).
///
/// Живёт в Infrastructure, а не в MediatR-обработчике Application, по одной причине: и адресат
/// уведомления («все администраторы»), и подпись автора («кто прислал») читаются из таблиц Identity,
/// а до них дотягивается только этот слой. Тот же приём, что у <c>UpdateNotifier</c>.
/// </summary>
public class BugReportService(
    AppDbContext db, INotificationService notifications, GithubIssueClient github,
    ILogger<BugReportService> logger) : IBugReportService
{
    /// <summary>Источник уведомлений — по нему же они группируются в колокольчике.</summary>
    public const string NotificationSource = "Сообщения об ошибках";

    /// <summary>Экран администратора: уведомление ведёт туда, а не пытается заменить его собой.</summary>
    public const string AdminScreenLink = "/bug-reports";

    /// <summary>
    /// Потолок сообщения. Не про место в базе, а про то, что за ним начинается не сообщение об
    /// ошибке, а вставленный лог; текст такого размера всё равно никто не прочтёт целиком.
    /// </summary>
    public const int MessageLimit = 4000;

    /// <summary>
    /// Потолок техблока. Его собирает клиент, а не человек, и повлиять на размер автор не может —
    /// поэтому перебор НЕ отказ: сообщение принимается, техблок заменяется отметкой о том, что его
    /// не сохранили. Потерять слова пользователя из-за размера служебного приложения было бы
    /// худшим из двух исходов.
    /// </summary>
    public const int TechLimit = 128 * 1024;

    /// <summary>
    /// Потолок версии — ровно ширина колонки в базе. Найдено живой проверкой: без него
    /// администратор, вставивший в поле лишнее, получал «Внутреннюю ошибку сервера» вместо отказа,
    /// потому что до отказа дело не доходило — падала вставка.
    /// </summary>
    public const int VersionLimit = 32;

    public async Task<Guid> SubmitAsync(Guid authorId, string message, JsonElement? tech,
        string? screenshotBlobPath, CancellationToken ct = default)
    {
        var text = (message ?? "").Trim();
        if (text.Length == 0)
            throw new InvalidRequestException("Опишите, что произошло, — без описания сообщение бесполезно.");
        if (text.Length > MessageLimit)
            throw new InvalidRequestException(
                $"Описание длиннее {MessageLimit} символов. Оставьте главное: что делали, что ожидали, что получили.");

        var report = BugReport.Create(authorId, text, StoreTech(tech), Trim(screenshotBlobPath));
        db.Add(report);
        await db.SaveChangesAsync(ct);

        await NotifyAdminsAsync(report, ct);
        return report.Id;
    }

    public async Task<BugReportList> ListAsync(CancellationToken ct = default)
    {
        // Предел есть, постраничности нет — и об этом говорим вслух: «Total» больше отданного
        // означает, что часть сообщений в списке не видна, а других дорог к ним нет.
        var total = await db.Set<BugReport>().CountAsync(ct);
        var reports = await db.Set<BugReport>()
            .AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Take(IBugReportService.ListLimit)
            .ToListAsync(ct);

        var authors = await AuthorsAsync(reports.Select(r => r.AuthorId), ct);
        var configured = await github.IsConfiguredAsync(ct);
        return new BugReportList([.. reports.Select(r => new BugReportListItem(
            r.Id,
            authors.TryGetValue(r.AuthorId, out var a) ? a.Name : "удалённый пользователь",
            r.Status,
            Summary(r.Message),
            r.GithubIssueNumber,
            r.FixedInVersion,
            r.ScreenshotBlobPath is not null,
            r.CreatedAt))], total, configured);
    }

    /// <summary>
    /// Передать разработчикам: завести issue из текста, который администратор уже отредактировал.
    ///
    /// Тело берётся из сохранённой правки, а если её нет — из заготовки; и то и другое собирает
    /// <see cref="BugReportIssueText" />, так что «отправили не то, что видели» здесь невозможно.
    /// </summary>
    public async Task<BugReportDetail> ForwardToGithubAsync(
        Guid id, string title, string? body = null, CancellationToken ct = default)
    {
        var text = (title ?? "").Trim();
        if (text.Length == 0)
            throw new InvalidRequestException(
                "Напишите заголовок issue — по нему его будут искать в трекере.");
        if (text.Length > IBugReportService.TitleLimit)
            throw new InvalidRequestException(
                $"Заголовок длиннее {IBugReportService.TitleLimit} символов. Оставьте суть, " +
                "подробности уже есть в теле.");

        var report = await RequireAsync(id, ct);
        // Повторная отправка завела бы ВТОРОЙ issue о том же, и следа первого в системе не
        // осталось бы — номер перезаписался. Дубль в трекере убирают руками, поэтому отказ.
        if (report.GithubIssueNumber is { } already)
            throw new ConflictException(
                $"Это сообщение уже передано: issue #{already}. Повторная отправка завела бы второй.");

        // Текст с экрана сохраняем ДО отправки: он и уйдёт, и останется в системе — иначе в базе
        // лежал бы один текст, а в трекере другой, и сверить их было бы нечем.
        if (!string.IsNullOrWhiteSpace(body) && body != report.IssueDraft)
        {
            report.SaveDraft(body);
            await db.SaveChangesAsync(ct);
        }

        var tech = ParseTech(report.TechContext);
        var outgoing = report.IssueDraft
                       ?? BugReportIssueText.Build(report.Message, tech, report.ScreenshotBlobPath is not null);

        // Сеть ПЕРЕД записью: откажет GitHub — в системе ничего не поменялось, и повторить можно
        // той же кнопкой. Обратный порядок оставлял бы сообщение «переданным» без issue.
        var created = await github.CreateAsync(text, outgoing, ct);

        await RecordForwardedAsync(report, created, ct);

        await NotifyAuthorAsync(report, "Ваше сообщение: передано разработчикам",
            $"«{Summary(report.Message)}» — заведена задача #{created.Number}.", ct,
            linkUrl: created.Url, linkLabel: $"Открыть issue #{created.Number}");
        return await DetailAsync(report, ct);
    }

    /// <summary>
    /// Записать «передано» — с оглядкой на то, что issue УЖЕ создан и отменить это нельзя.
    ///
    /// Два опасных исхода, и оба заканчиваются дублем в трекере, если промолчать:
    ///
    /// 1. Запись не удалась (обрыв соединения, отмена запроса). Номер известен только этому вызову,
    ///    и потерять его — значит гарантировать второй issue при следующей попытке. Поэтому номер
    ///    и адрес уходят в журнал уровнем Error и в сам текст отказа: администратор видит, что
    ///    задача создана, и не жмёт кнопку повторно.
    /// 2. Пока шла отправка, сообщение передал кто-то ещё (вторая вкладка, второй администратор).
    ///    Проверка в начале — обычное «прочитал и записал», и от гонки не защищает. Условие
    ///    <c>GithubIssueNumber == null</c> ПРЯМО В UPDATE делает запись состязательной: чей issue
    ///    записался первым, тот и остался, а проигравший узнаёт номер своего дубля.
    /// </summary>
    private async Task RecordForwardedAsync(BugReport report, CreatedIssue created, CancellationToken ct)
    {
        int updated;
        try
        {
            updated = await db.Set<BugReport>()
                .Where(r => r.Id == report.Id && r.GithubIssueNumber == null)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(r => r.GithubIssueNumber, created.Number)
                    .SetProperty(r => r.GithubIssueUrl, created.Url)
                    .SetProperty(r => r.Status, BugReportStatus.Forwarded)
                    .SetProperty(r => r.UpdatedAt, DateTimeOffset.UtcNow), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Issue #{Number} создан ({Url}), но записать это в сообщение {Report} не удалось",
                created.Number, created.Url, report.Id);
            throw new ConflictException(
                $"Issue #{created.Number} создан ({created.Url}), но отметить сообщение переданным " +
                "не удалось. НЕ отправляйте повторно — заведётся второй; сообщите разработчикам номер.");
        }

        if (updated == 0)
        {
            logger.LogError(
                "Гонка при передаче сообщения {Report}: issue #{Number} ({Url}) создан вторым",
                report.Id, created.Number, created.Url);
            throw new ConflictException(
                $"Пока шла отправка, сообщение передали другим окном. Только что созданный " +
                $"issue #{created.Number} — дубль ({created.Url}), закройте его в GitHub.");
        }

        // Сущность в трекере осталась со старыми значениями: ExecuteUpdate пишет мимо него.
        await db.Entry(report).ReloadAsync(ct);
    }

    public async Task<BugReportDetail> GetAsync(Guid id, CancellationToken ct = default)
        => await DetailAsync(await RequireAsync(id, ct), ct);

    public async Task<BugReportDetail> SaveDraftAsync(Guid id, string? text, CancellationToken ct = default)
    {
        var report = await RequireAsync(id, ct);
        report.SaveDraft(text);
        await db.SaveChangesAsync(ct);
        return await DetailAsync(report, ct);
    }

    public async Task<BugReportDetail> MarkFixedAsync(Guid id, string version, CancellationToken ct = default)
    {
        var text = (version ?? "").Trim();
        if (text.Length == 0)
            throw new InvalidRequestException("Укажите версию, в которой исправлено, — её увидит автор сообщения.");
        if (text.Length > VersionLimit)
            throw new InvalidRequestException(
                $"Слишком длинно для номера версии (больше {VersionLimit} символов). Ожидается вид 0.145.0.");

        var report = await RequireAsync(id, ct);
        report.MarkFixed(text);
        await db.SaveChangesAsync(ct);

        await NotifyAuthorAsync(report, "Ваше сообщение: исправлено",
            $"«{Summary(report.Message)}» — исправлено в версии {text}.", ct);
        return await DetailAsync(report, ct);
    }

    public async Task<BugReportDetail> RejectAsync(Guid id, CancellationToken ct = default)
    {
        var report = await RequireAsync(id, ct);
        report.Reject();
        await db.SaveChangesAsync(ct);

        await NotifyAuthorAsync(report, "Ваше сообщение: отклонено",
            $"«{Summary(report.Message)}» — разработки по нему не будет. " +
            "Если вопрос остался, обратитесь к администратору.", ct);
        return await DetailAsync(report, ct);
    }

    public async Task<BugReportDetail> ReopenAsync(Guid id, CancellationToken ct = default)
    {
        var report = await RequireAsync(id, ct);
        report.Reopen();
        await db.SaveChangesAsync(ct);
        // Автору не сообщаем: «вернули в разбор» — это исправление ошибки администратора, а не
        // событие в жизни сообщения. Дёргать человека тем, что кто-то нажал не ту кнопку, незачем.
        return await DetailAsync(report, ct);
    }

    // ── Внутреннее ──────────────────────────────────────────────────────────

    private async Task<BugReport> RequireAsync(Guid id, CancellationToken ct)
        => await db.Set<BugReport>().FirstOrDefaultAsync(r => r.Id == id, ct)
           ?? throw new NotFoundException("Сообщение об ошибке не найдено.");

    private async Task<BugReportDetail> DetailAsync(BugReport r, CancellationToken ct)
    {
        var authors = await AuthorsAsync([r.AuthorId], ct);
        var tech = ParseTech(r.TechContext);
        return new BugReportDetail(
            r.Id,
            authors.TryGetValue(r.AuthorId, out var a) ? a.Name : "удалённый пользователь",
            authors.TryGetValue(r.AuthorId, out var b) ? b.Email : null,
            r.Message,
            tech,
            r.ScreenshotBlobPath,
            r.Status,
            r.IssueDraft ?? BugReportIssueText.Build(r.Message, tech, r.ScreenshotBlobPath is not null),
            r.IssueDraft is not null,
            r.GithubIssueNumber,
            r.GithubIssueUrl,
            r.FixedInVersion,
            r.CreatedAt,
            r.UpdatedAt);
    }

    /// <summary>
    /// Техблок на хранение: к присланному клиентом добавляем версию СЕРВЕРА.
    ///
    /// Обе версии нужны порознь: SPA живёт во вкладке и переживает обновление сервера, поэтому
    /// «версия клиента» — это то, чем человек пользовался, а «версия сервера» — то, что отвечало на
    /// его запросы. Расхождение этих двух само по себе объясняет часть сообщений.
    /// </summary>
    private static string? StoreTech(JsonElement? tech)
    {
        var (version, commit) = AppVersion.SplitInformational(AppVersion.InformationalOfEntryAssembly());
        var server = new { version, commit = commit.Length > 7 ? commit[..7] : commit };

        var payload = new Dictionary<string, object?>();
        if (tech is { ValueKind: JsonValueKind.Object } t)
            foreach (var p in t.EnumerateObject())
                payload[p.Name] = p.Value;
        payload["server"] = server;

        var json = JsonSerializer.Serialize(payload);
        if (json.Length <= TechLimit) return json;

        // Слова пользователя дороже техблока: сообщение принимаем, а на месте техблока оставляем
        // отметку — иначе администратор гадал бы, почему у одного сообщения контекста нет.
        return JsonSerializer.Serialize(new
        {
            server,
            dropped = $"Техблок превысил {TechLimit / 1024} КБ и не сохранён.",
        });
    }

    private static JsonElement? ParseTech(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    /// <summary>Первая строка сообщения (для списка и для текста уведомления).</summary>
    private static string Summary(string message)
    {
        var line = message.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        return line.Length <= 120 ? line : line[..120].TrimEnd() + "…";
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<Dictionary<Guid, (string Name, string? Email)>> AuthorsAsync(
        IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) return [];
        return await db.Users
            .Where(u => wanted.Contains(u.Id))
            .ToDictionaryAsync(
                u => u.Id,
                u => (Name: string.IsNullOrWhiteSpace(u.DisplayName) ? (u.Email ?? "") : u.DisplayName,
                      Email: u.Email),
                ct);
    }

    /// <summary>
    /// Уведомляем администраторов ЛИЧНО — каждого своей записью, а не одной общесистемной.
    ///
    /// Причина та же, что у сообщений об обновлении (issue #813): у общесистемного уведомления
    /// состояние прочтения общее на всех, и первый, кто смахнул его крестиком, снял бы запись со
    /// всех остальных (issue #821). Сообщение об ошибке — работа, у неё должен быть адресат.
    /// </summary>
    private async Task NotifyAdminsAsync(BugReport report, CancellationToken ct)
    {
        var adminIds = await db.UserRoles
            .Where(ur => db.Roles.Any(r => r.Id == ur.RoleId && r.Name == "Admin"))
            .Select(ur => ur.UserId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var id in adminIds)
        {
            // Автор-администратор о собственном сообщении не уведомляется: он только что нажал
            // «Отправить» и результат уже видел.
            if (id == report.AuthorId) continue;
            await notifications.PublishAsync(NotificationSeverity.Warning,
                "Сообщение об ошибке",
                Summary(report.Message),
                NotificationSource, userId: id,
                linkUrl: AdminScreenLink, linkLabel: "Открыть сообщения", ct: ct);
        }
    }

    private async Task NotifyAuthorAsync(BugReport report, string title, string message,
        CancellationToken ct, string? linkUrl = null, string? linkLabel = null)
        => await notifications.PublishAsync(NotificationSeverity.Info, title, message,
            NotificationSource, userId: report.AuthorId,
            linkUrl: linkUrl, linkLabel: linkLabel, ct: ct);
}
