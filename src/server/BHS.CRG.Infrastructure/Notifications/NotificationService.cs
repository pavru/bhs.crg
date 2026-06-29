using System.Linq.Expressions;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Notifications;

public class NotificationService(AppDbContext db, ILogger<NotificationService> logger) : INotificationService
{
    private const int MaxKept = 300;

    // Видимые пользователю: личные (его userId) + общесистемные (null).
    private static Expression<Func<Notification, bool>> VisibleTo(Guid userId)
        => n => n.UserId == userId || n.UserId == null;

    public async Task PublishAsync(NotificationSeverity severity, string title, string message,
        string? source = null, Guid? userId = null, string? linkUrl = null, string? linkLabel = null,
        CancellationToken ct = default)
    {
        var n = Notification.Create(severity, title, message, source, userId, linkUrl, linkLabel);
        db.Notifications.Add(n);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Уведомление [{Severity}] {Title} ({Source}) user={User}", severity, title, source, userId);

        await PruneAsync(ct);
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        var total = await db.Notifications.CountAsync(ct);
        if (total <= MaxKept) return;
        var idsToRemove = await db.Notifications
            .OrderByDescending(n => n.CreatedAt)
            .Skip(MaxKept)
            .Select(n => n.Id)
            .ToListAsync(ct);
        await db.Notifications.Where(n => idsToRemove.Contains(n.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<NotificationDto>> GetAsync(Guid userId, bool unreadOnly = false, int take = 100, CancellationToken ct = default)
    {
        var q = db.Notifications.AsNoTracking().Where(VisibleTo(userId));
        if (unreadOnly) q = q.Where(n => !n.IsRead);
        return await q
            .OrderByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(take, 1, MaxKept))
            .Select(n => new NotificationDto(n.Id, n.Severity, n.Title, n.Message, n.Source, n.LinkUrl, n.LinkLabel, n.IsRead, n.CreatedAt))
            .ToListAsync(ct);
    }

    public Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default)
        => db.Notifications.Where(VisibleTo(userId)).CountAsync(n => !n.IsRead, ct);

    public async Task MarkReadAsync(Guid id, Guid userId, CancellationToken ct = default)
        => await db.Notifications.Where(VisibleTo(userId)).Where(n => n.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);

    public Task MarkAllReadAsync(Guid userId, CancellationToken ct = default)
        => db.Notifications.Where(VisibleTo(userId)).Where(n => !n.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), ct);

    public Task DismissAsync(Guid id, Guid userId, CancellationToken ct = default)
        => db.Notifications.Where(VisibleTo(userId)).Where(n => n.Id == id).ExecuteDeleteAsync(ct);

    public Task ClearAsync(Guid userId, CancellationToken ct = default)
        => db.Notifications.Where(VisibleTo(userId)).ExecuteDeleteAsync(ct);
}
