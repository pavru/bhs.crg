using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Общесистемное уведомление (<c>UserId == null</c>) видно всем, а отметки «прочитано» и «скрыто»
/// принадлежат каждому по отдельности — issue #821. До правки признак лежал на самой записи:
/// один прочитал — гасло у всех, один смахнул — исчезало у всех, «Очистить все» стирало
/// уведомления всей компании.
///
/// Проверяем всегда ДВУСТОРОННЕ: что действие сработало у того, кто его сделал, И что у соседа
/// ничего не изменилось. Односторонняя проверка пропустила бы ровно тот дефект, о котором issue.
/// </summary>
[Collection("Integration")]
public class NotificationPerUserStateTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Предел хранения на корзину — тот же, что в NotificationService.</summary>
    private const int MaxKept = 300;

    private static INotificationService Service(IServiceScope s)
        => s.ServiceProvider.GetRequiredService<INotificationService>();

    private async Task<Guid> CreateUserAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест" };
        Assert.True((await um.CreateAsync(user, "Passw0rd!")).Succeeded);
        return user.Id;
    }

    private async Task<Guid> PublishAsync(Guid? userId, string title = "Событие")
    {
        using var scope = fixture.Services.CreateScope();
        await Service(scope).PublishAsync(NotificationSeverity.Info, title, "текст", "Тест", userId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Notifications.Where(n => n.Title == title)
            .OrderByDescending(n => n.CreatedAt).Select(n => n.Id).FirstAsync();
    }

    private async Task<(bool Visible, bool IsRead)> StateFor(Guid userId, Guid notificationId)
    {
        using var scope = fixture.Services.CreateScope();
        var item = (await Service(scope).GetAsync(userId)).FirstOrDefault(x => x.Id == notificationId);
        return (item is not null, item?.IsRead ?? false);
    }

    /// <summary>
    /// Непрочитанное — СВОИМИ id, а не числом: под тестовым хостом работает health-мониторинг и
    /// публикует общесистемные уведомления о доступности компонент. Абсолютный счётчик от них
    /// разъезжается, и падение выглядело бы дефектом уведомлений, а не помехой в фикстуре.
    /// </summary>
    private async Task<HashSet<Guid>> UnreadIdsAsync(Guid userId)
    {
        using var scope = fixture.Services.CreateScope();
        var items = await Service(scope).GetAsync(userId, unreadOnly: true, take: 300);
        return items.Select(x => x.Id).ToHashSet();
    }

    [Fact]
    public async Task MarkRead_OfSystemWide_DoesNotMarkItReadForOthers()
    {
        var alice = await CreateUserAsync();
        var bob = await CreateUserAsync();
        var id = await PublishAsync(null, "Общесистемное");

        using (var scope = fixture.Services.CreateScope())
            await Service(scope).MarkReadAsync(id, alice);

        var a = await StateFor(alice, id);
        var b = await StateFor(bob, id);

        Assert.True(a.Visible);
        Assert.True(a.IsRead);
        Assert.DoesNotContain(id, await UnreadIdsAsync(alice));

        Assert.True(b.Visible);
        Assert.False(b.IsRead);
        Assert.Contains(id, await UnreadIdsAsync(bob));
    }

    [Fact]
    public async Task MarkAllRead_DoesNotTouchOtherUsers()
    {
        var alice = await CreateUserAsync();
        var bob = await CreateUserAsync();
        var first = await PublishAsync(null, "Первое");
        var second = await PublishAsync(null, "Второе");

        using (var scope = fixture.Services.CreateScope())
            await Service(scope).MarkAllReadAsync(alice);

        var aliceUnread = await UnreadIdsAsync(alice);
        Assert.DoesNotContain(first, aliceUnread);
        Assert.DoesNotContain(second, aliceUnread);
        Assert.True((await StateFor(alice, second)).IsRead);

        var bobUnread = await UnreadIdsAsync(bob);
        Assert.Contains(first, bobUnread);
        Assert.Contains(second, bobUnread);
    }

    [Fact]
    public async Task Dismiss_OfSystemWide_HidesItOnlyForThatUser()
    {
        var alice = await CreateUserAsync();
        var bob = await CreateUserAsync();
        var id = await PublishAsync(null, "Общесистемное");

        using (var scope = fixture.Services.CreateScope())
            await Service(scope).DismissAsync(id, alice);

        Assert.False((await StateFor(alice, id)).Visible);
        Assert.True((await StateFor(bob, id)).Visible);

        // Скрытое не должно попадать и в непрочитанное: колокольчик показывал бы цифру без строки.
        Assert.DoesNotContain(id, await UnreadIdsAsync(alice));

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Notifications.AnyAsync(n => n.Id == id));   // сама запись цела
    }

    [Fact]
    public async Task Clear_RemovesOnlyOwnNotifications()
    {
        var alice = await CreateUserAsync();
        var bob = await CreateUserAsync();
        var systemWide = await PublishAsync(null, "Общесистемное");
        var alicePersonal = await PublishAsync(alice, "Личное Алисы");
        var bobPersonal = await PublishAsync(bob, "Личное Боба");

        using (var scope = fixture.Services.CreateScope())
            await Service(scope).ClearAsync(alice);

        Assert.False((await StateFor(alice, systemWide)).Visible);
        Assert.False((await StateFor(alice, alicePersonal)).Visible);

        Assert.True((await StateFor(bob, systemWide)).Visible);
        Assert.True((await StateFor(bob, bobPersonal)).Visible);

        var bobUnread = await UnreadIdsAsync(bob);
        Assert.Contains(systemWide, bobUnread);
        Assert.Contains(bobPersonal, bobUnread);
    }

    [Fact]
    public async Task Dismiss_OfPersonal_RemovesRow_AndForeignPersonalIsUntouched()
    {
        var alice = await CreateUserAsync();
        var bob = await CreateUserAsync();
        var alicePersonal = await PublishAsync(alice, "Личное Алисы");
        var bobPersonal = await PublishAsync(bob, "Личное Боба");

        using (var scope = fixture.Services.CreateScope())
        {
            var svc = Service(scope);
            await svc.DismissAsync(alicePersonal, alice);
            await svc.DismissAsync(bobPersonal, alice);   // чужое личное — не наше дело
        }

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Notifications.AnyAsync(n => n.Id == alicePersonal));
        Assert.True(await db.Notifications.AnyAsync(n => n.Id == bobPersonal));
        Assert.True((await StateFor(bob, bobPersonal)).Visible);
    }

    [Fact]
    public async Task MarkRead_IsIdempotent_AndSurvivesRepeatedCalls()
    {
        var alice = await CreateUserAsync();
        var id = await PublishAsync(null, "Общесистемное");

        using (var scope = fixture.Services.CreateScope())
        {
            var svc = Service(scope);
            await svc.MarkReadAsync(id, alice);
            await svc.MarkReadAsync(id, alice);   // поллинг колокольчика повторяет отметку
        }

        Assert.True((await StateFor(alice, id)).IsRead);

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.NotificationUserStates.CountAsync(s => s.NotificationId == id && s.UserId == alice));
    }

    [Fact]
    public async Task DeletingNotification_TakesItsStatesWithIt()
    {
        var alice = await CreateUserAsync();
        var id = await PublishAsync(null, "Общесистемное");

        using (var scope = fixture.Services.CreateScope())
            await Service(scope).MarkReadAsync(id, alice);

        using var scope2 = fixture.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Notifications.Where(n => n.Id == id).ExecuteDeleteAsync();

        Assert.False(await db.NotificationUserStates.AnyAsync(s => s.NotificationId == id));
    }

    /// <summary>
    /// Предел числа хранимых уведомлений — на корзину, а не общий: иначе поток чужих уведомлений
    /// о генерации вытеснял бы важное (issue #821).
    /// </summary>
    [Fact]
    public async Task Prune_KeepsLimitPerRecipient_NotGlobally()
    {
        var alice = await CreateUserAsync();
        var bob = await CreateUserAsync();

        // Порядок принципиален: уведомление Алисы — САМОЕ СТАРОЕ. При общем пределе на всех его и
        // вытеснил бы поток Боба; при пределе на корзину оно единственное в своей и остаётся.
        var important = await PublishAsync(alice, "Важное Алисе");

        using (var scope = fixture.Services.CreateScope())
        {
            var svc = Service(scope);
            for (var i = 0; i < MaxKept + 5; i++)
                await svc.PublishAsync(NotificationSeverity.Info, $"Боб {i}", "текст", "Тест", bob);
        }

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(MaxKept, await db.Notifications.CountAsync(n => n.UserId == bob));
        Assert.True(await db.Notifications.AnyAsync(n => n.Id == important));
    }
}
