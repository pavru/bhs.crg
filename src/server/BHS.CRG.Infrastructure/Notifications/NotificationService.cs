using System.Collections.Concurrent;
using System.Linq.Expressions;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Notifications;

/// <summary>
/// Список уведомлений пользователя и его состояние.
///
/// Ключевое: «прочитано»/«скрыто» — состояние пары (уведомление, пользователь) и живёт в
/// notification_user_states. Раньше оно лежало на самой записи, и любая отметка у одного
/// пользователя срабатывала у всех (issue #821). Строка состояния заводится лениво, поэтому
/// «нет строки» == «не прочитано и не скрыто», а список читается левым соединением.
/// </summary>
public class NotificationService(
    AppDbContext db, ILogger<NotificationService> logger, INotificationAudience audiences) : INotificationService
{
    /// <summary>
    /// Сколько уведомлений держим — НА КОРЗИНУ (на каждого получателя и отдельно на общесистемные).
    ///
    /// Колокольчик — ящик входящих, а не журнал событий: старое вытесняется без следа в таблице.
    /// Ровно <see cref="MaxKept"/> записей в корзине значит «история обрезана», и хронологию событий
    /// по ней не восстановить (issue #919: поток мониторинга вытеснил две недели и был принят за
    /// начало сбоев). Вытеснение молодых записей журнал отмечает предупреждением — см.
    /// <see cref="NotificationEviction"/>.
    /// </summary>
    private const int MaxKept = 300;

    // Когда корзина последний раз предупреждала о потоке. Статически: сервис живёт запрос, а
    // ограничение частоты — всё время процесса. Guid.Empty — корзина общесистемных. Гонка двух
    // одновременных подрезок даст в худшем случае две строки вместо одной.
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> LastFlowWarning = new();

    // Адресовано пользователю: личное (его userId) или общесистемное, чья аудитория ему подходит.
    // keys — права пользователя и коды доступных ему модулей (INotificationAudience).
    private static Expression<Func<Notification, bool>> AddressedTo(Guid userId, IReadOnlyCollection<string> keys)
        => n => n.UserId == userId
             || (n.UserId == null && (n.Audience == null || keys.Contains(n.Audience)));

    // То же, но общесистемные — только выпущенные после появления учётной записи.
    private Expression<Func<Notification, bool>> VisibleSince(Guid userId, IReadOnlyCollection<string> keys)
        => n => n.UserId == userId
             || (n.UserId == null
                 && (n.Audience == null || keys.Contains(n.Audience))
                 && n.CreatedAt >= db.Users.Where(u => u.Id == userId).Select(u => u.CreatedAt).FirstOrDefault());

    public async Task<Guid> PublishAsync(NotificationSeverity severity, string title, string message,
        string? source = null, Guid? userId = null, string? linkUrl = null, string? linkLabel = null,
        string? audience = null, CancellationToken ct = default)
    {
        // Несуществующая аудитория — отказ на публикации, а не пустой список у всех потом.
        if (audience is not null) audiences.EnsureDeclared(audience);

        var n = Notification.Create(severity, title, message, source, userId, linkUrl, linkLabel, audience);
        db.Notifications.Add(n);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Уведомление [{Severity}] {Title} ({Source}) user={User} аудитория={Audience}",
            severity, title, source, userId, audience ?? "все вошедшие");

        await PruneAsync(userId, n.CreatedAt, ct);
        return n.Id;
    }

    /// <summary>
    /// Подрезает ТУ корзину, в которую только что положили: у каждого пользователя свои
    /// <see cref="MaxKept"/> и отдельно столько же общесистемных. Общий предел на всех вытеснял бы
    /// важное чужим потоком уведомлений о генерации и распознавании.
    /// </summary>
    private async Task PruneAsync(Guid? userId, DateTimeOffset newestKept, CancellationToken ct)
    {
        // Отдельные ветки, а не `n.UserId == userId`: с nullable-параметром EF сгенерировал бы
        // сравнение `= NULL`, которое в SQL не истинно никогда, и корзина общесистемных не чистилась бы.
        var bucket = userId is null
            ? db.Notifications.Where(n => n.UserId == null)
            : db.Notifications.Where(n => n.UserId == userId);

        var total = await bucket.CountAsync(ct);
        if (total <= MaxKept) return;

        // С последней оставляемой: её дата — горизонт истории, который останется после подрезки.
        var tail = await bucket
            .OrderByDescending(n => n.CreatedAt)
            .Skip(MaxKept - 1)
            .Select(n => new { n.Id, n.CreatedAt })
            .ToListAsync(ct);
        var horizon = tail[0].CreatedAt;
        var evicted = tail.Skip(1).ToList();   // от самой молодой к самой старой
        var idsToRemove = evicted.Select(e => e.Id).ToList();
        await db.Notifications.Where(n => idsToRemove.Contains(n.Id)).ExecuteDeleteAsync(ct);

        var flow = NotificationEviction.IsFlow(evicted[0].CreatedAt, newestKept);
        var key = userId ?? Guid.Empty;
        var now = DateTimeOffset.UtcNow;
        if (!NotificationEviction.ShouldWarn(flow, LastFlowWarning.TryGetValue(key, out var last) ? last : null, now)) return;
        LastFlowWarning[key] = now;

        logger.LogWarning(
            "Уведомления вытесняются потоком: корзина {Bucket} удалила {Removed} записей моложе {Days} дней; " +
            "история теперь начинается с {Horizon:u}. Хронологию событий по колокольчику не восстанавливать — " +
            "ищите издателя, который публикует слишком часто",
            userId?.ToString() ?? "общесистемных", evicted.Count, NotificationEviction.FlowWindow.TotalDays, horizon);
    }

    public async Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly = false, int take = 100, CancellationToken ct = default)
        => await VisibleTo(userId, await audiences.KeysForAsync(userId, ct), unreadOnly)
            .Take(Math.Clamp(take, 1, MaxKept)).ToListAsync(ct);

    public async Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default)
        => await VisibleTo(userId, await audiences.KeysForAsync(userId, ct), unreadOnly: true).CountAsync(ct);

    /// <summary>
    /// Видимые пользователю уведомления с ЕГО состоянием; скрытые им отсеяны.
    ///
    /// Соединение левое: строки состояния заводятся лениво, и «нет строки» означает «не прочитано
    /// и не скрыто» — внутреннее соединение потеряло бы всё непрочитанное, то есть ровно то, ради
    /// чего список открывают. Проекция сразу в DTO, а не в промежуточный тип: сортировку по полю
    /// пользовательской структуры EF не переводит и падает уже на выполнении запроса.
    ///
    /// Общесистемные отсекаются по дате заведения учётной записи — подзапросом, а не отдельным
    /// обращением: список опрашивают поллингом, лишний круг к базе тут не бесплатный. Отсечка
    /// нужна ровно из-за ленивых строк состояния: «нет строки» = «не прочитано», и без неё новый
    /// сотрудник открывал бы колокольчик с чужим прошлым, помеченным как непрочитанное.
    ///
    /// Аудитория проверяется здесь же, при чтении: список всегда соответствует СЕГОДНЯШНИМ правам
    /// (ТЗ AUTH-13) — право выдали, и прошлые уведомления этой аудитории появились; отозвали —
    /// исчезли.
    /// </summary>
    private IQueryable<NotificationDto> VisibleTo(Guid userId, IReadOnlyCollection<string> keys, bool unreadOnly)
        => from n in db.Notifications.AsNoTracking().Where(VisibleSince(userId, keys))
           join st in db.NotificationUserStates.AsNoTracking().Where(x => x.UserId == userId)
               on n.Id equals st.NotificationId into states
           from st in states.DefaultIfEmpty()
           where st == null || !st.IsDismissed
           where !unreadOnly || st == null || !st.IsRead
           orderby n.CreatedAt descending
           select new NotificationDto(n.Id, n.Severity, n.Title, n.Message, n.Source,
               n.LinkUrl, n.LinkLabel, st != null && st.IsRead, n.CreatedAt);

    public async Task MarkReadAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var keys = await audiences.KeysForAsync(userId, ct);
        var visible = await db.Notifications.Where(AddressedTo(userId, keys)).AnyAsync(n => n.Id == id, ct);
        if (!visible) return;
        await UpsertStateAsync(id, userId, isRead: true, isDismissed: false, ct);
    }

    public async Task MarkAllReadAsync(Guid userId, CancellationToken ct = default)
    {
        // Аудитория учитывается и здесь: «прочитать все» не должно заводить состояние на записи,
        // которых пользователь не видел. Иначе выданное позже право открыло бы прошлые уведомления
        // сразу прочитанными — то есть не открыло бы.
        var keys = (await audiences.KeysForAsync(userId, ct)).ToArray();

        // Одним запросом: состояние заводится сразу для всех видимых уведомлений, у которых его нет.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notification_user_states ("Id", "NotificationId", "UserId", "IsRead", "IsDismissed", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), n."Id", {userId}, TRUE, FALSE, now(), now()
            FROM notifications n
            WHERE (n."UserId" = {userId}
                   OR (n."UserId" IS NULL AND (n."Audience" IS NULL OR n."Audience" = ANY({keys}))))
              AND EXISTS (SELECT 1 FROM "AspNetUsers" u WHERE u."Id" = {userId})
            ON CONFLICT ("NotificationId", "UserId") DO UPDATE
                SET "IsRead" = TRUE, "UpdatedAt" = now()
            """, ct);
    }

    public async Task DismissAsync(Guid id, Guid userId, CancellationToken ct = default)
    {
        var owner = await db.Notifications.Where(n => n.Id == id).Select(n => n.UserId).FirstOrDefaultAsync(ct);
        if (owner == userId)
        {
            // Личное уведомление: владелец один, удаляем физически (состояния уходят каскадом).
            await db.Notifications.Where(n => n.Id == id && n.UserId == userId).ExecuteDeleteAsync(ct);
            return;
        }

        var keys = await audiences.KeysForAsync(userId, ct);
        var isSystemWide = await db.Notifications.Where(AddressedTo(userId, keys))
            .AnyAsync(n => n.Id == id && n.UserId == null, ct);
        if (!isSystemWide) return;   // чужое личное или не его аудитория — не наше дело

        // Общесистемное: прячем только у этого пользователя, у остальных остаётся.
        await UpsertStateAsync(id, userId, isRead: true, isDismissed: true, ct);
    }

    public async Task ClearAsync(Guid userId, CancellationToken ct = default)
    {
        // Обе половины — в одной транзакции, и НЕОБРАТИМАЯ идёт второй. Иначе отказ на втором шаге
        // (оборванное соединение, откатившийся запрос) оставлял бы личные уведомления удалёнными
        // навсегда, а общесистемные — на экране; повтор «Очистить все» удалённое уже не вернёт.
        var keys = (await audiences.KeysForAsync(userId, ct)).ToArray();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notification_user_states ("Id", "NotificationId", "UserId", "IsRead", "IsDismissed", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), n."Id", {userId}, TRUE, TRUE, now(), now()
            FROM notifications n
            WHERE n."UserId" IS NULL
              AND (n."Audience" IS NULL OR n."Audience" = ANY({keys}))
              AND EXISTS (SELECT 1 FROM "AspNetUsers" u WHERE u."Id" = {userId})
            ON CONFLICT ("NotificationId", "UserId") DO UPDATE
                SET "IsRead" = TRUE, "IsDismissed" = TRUE, "UpdatedAt" = now()
            """, ct);

        await db.Notifications.Where(n => n.UserId == userId).ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Заводит или обновляет строку состояния. Через ON CONFLICT, а не «прочитать-и-записать»:
    /// колокольчик опрашивает список поллингом, две отметки подряд гонятся за одну и ту же пару.
    /// Флаги только поднимаются (OR) — чтобы «прочитано» не сбрасывалось более поздней отметкой.
    ///
    /// INSERT ... SELECT с проверкой пользователя, а не VALUES: userId приходит из токена, а токен
    /// живёт минуты и переживает удаление учётной записи. Без проверки первая же отметка такого
    /// пользователя падала бы нарушением внешнего ключа — ответом 500 там, где до появления
    /// строк состояния просто ничего не происходило.
    /// </summary>
    private Task UpsertStateAsync(Guid notificationId, Guid userId, bool isRead, bool isDismissed, CancellationToken ct)
        => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notification_user_states ("Id", "NotificationId", "UserId", "IsRead", "IsDismissed", "CreatedAt", "UpdatedAt")
            SELECT gen_random_uuid(), {notificationId}, {userId}, {isRead}, {isDismissed}, now(), now()
            WHERE EXISTS (SELECT 1 FROM "AspNetUsers" u WHERE u."Id" = {userId})
            ON CONFLICT ("NotificationId", "UserId") DO UPDATE
                SET "IsRead" = notification_user_states."IsRead" OR EXCLUDED."IsRead",
                    "IsDismissed" = notification_user_states."IsDismissed" OR EXCLUDED."IsDismissed",
                    "UpdatedAt" = now()
            """, ct);
}
