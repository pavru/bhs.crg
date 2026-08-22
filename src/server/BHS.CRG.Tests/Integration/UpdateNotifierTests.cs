using BHS.CRG.Application.Notifications;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Updates;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Кому достаётся сообщение о новой версии (issue #813).
///
/// Проверяется здесь, а не на глаз, потому что цена ошибки скрытая: у ОБЩЕСИСТЕМНОГО уведомления
/// (<c>UserId == null</c>) состояние прочтения общее на всех — любой пользователь пометил
/// прочитанным или смахнул, и записи не стало ни у кого. Опубликуй мы так, единственный, кто может
/// обновить систему, узнавал бы последним или никогда, а выглядело бы это как «уведомления
/// работают».
/// </summary>
[Collection("Integration")]
public class UpdateNotifierTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Guid> CreateUserAsync(string display, string? role)
    {
        using var scope = fixture.Services.CreateScope();
        var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var rm = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (role is not null && !await rm.RoleExistsAsync(role))
            Assert.True((await rm.CreateAsync(new IdentityRole<Guid>(role))).Succeeded);

        var email = $"{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = display };
        Assert.True((await um.CreateAsync(user, "Passw0rd!")).Succeeded);
        if (role is not null) Assert.True((await um.AddToRoleAsync(user, role)).Succeeded);
        return user.Id;
    }

    private static UpdateNotifier Notifier(IServiceScope s)
        => new(s.ServiceProvider.GetRequiredService<AppDbContext>(),
               s.ServiceProvider.GetRequiredService<INotificationService>());

    /// <summary>
    /// Оставить администратором только указанных.
    ///
    /// Нужно потому, что учётные записи в тестовой базе намеренно не очищаются между классами, а
    /// приложение при старте выдаёт роль Admin КАЖДОМУ пользователю без роли (легаси-миграция в
    /// Program.cs). За десятки прогонов админами становятся сотни накопившихся записей, рассылка
    /// упирается в предел хранения уведомлений (300), и проверка «пришло ли нашему» перестаёт быть
    /// проверкой. Роли снимаются только в рамках теста; следующий старт фикстуры вернёт их.
    /// </summary>
    private async Task KeepOnlyAdminsAsync(params Guid[] keep)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var adminRoleId = await db.Roles.Where(r => r.Name == "Admin").Select(r => r.Id).FirstAsync();
        await db.UserRoles
            .Where(ur => ur.RoleId == adminRoleId && !keep.Contains(ur.UserId))
            .ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Notifies_AdminsOnly_AndPersonally()
    {
        var adminId = await CreateUserAsync("Администратор", "Admin");
        var userId = await CreateUserAsync("Пользователь", "User");
        await KeepOnlyAdminsAsync(adminId);

        using (var scope = fixture.Services.CreateScope())
            await Notifier(scope).NotifyAsync("0.138.0", "0.137.1", default);

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();

        // Проверяем адресно, по созданным здесь пользователям: учётные записи в тестовой базе
        // намеренно НЕ очищаются между классами (их создают многие тесты), так что счёт «всех
        // уведомлений об обновлении» ничего бы не сказал.
        var forAdmin = await db.Notifications.AsNoTracking()
            .Where(n => n.Source == UpdateNotifier.Source && n.UserId == adminId).ToListAsync();
        var one = Assert.Single(forAdmin);
        Assert.Contains("0.138.0", one.Title);
        Assert.Contains("0.137.1", one.Message);

        // Ключевое: адресно, а НЕ общесистемно (UserId == null) — иначе первый же прочитавший
        // погасил бы запись у администратора.
        Assert.Empty(await db.Notifications.AsNoTracking()
            .Where(n => n.Source == UpdateNotifier.Source && n.UserId == null).ToListAsync());

        // Обычному пользователю сообщение не адресовано — но и не спрятано: номер версии он видит
        // в подвале боковой панели, пассивно.
        var notifier = check.ServiceProvider.GetRequiredService<INotificationService>();
        Assert.Empty((await notifier.GetAsync(userId)).Where(n => n.Source == UpdateNotifier.Source));
        Assert.Single((await notifier.GetAsync(adminId)).Where(n => n.Source == UpdateNotifier.Source));
    }

    [Fact]
    public async Task NextVersion_ReplacesPreviousMessage_InsteadOfPilingUp()
    {
        var adminId = await CreateUserAsync("Администратор", "Admin");
        await KeepOnlyAdminsAsync(adminId);

        using (var scope = fixture.Services.CreateScope())
        {
            await Notifier(scope).NotifyAsync("0.138.0", "0.137.1", default);
            await Notifier(scope).NotifyAsync("0.139.0", "0.137.1", default);
        }

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var sent = await db.Notifications.AsNoTracking()
            .Where(n => n.Source == UpdateNotifier.Source && n.UserId == adminId).ToListAsync();

        // К третьему выпуску в колокольчике лежали бы три записи об одном и том же, и свежая
        // терялась бы среди устаревших.
        var one = Assert.Single(sent);
        Assert.Contains("0.139.0", one.Title);
    }

    [Fact]
    public async Task WithoutAdmins_SendsNothing()
    {
        var userId = await CreateUserAsync("Пользователь", "User");
        await KeepOnlyAdminsAsync();   // администраторов не осталось вовсе

        using (var scope = fixture.Services.CreateScope())
            await Notifier(scope).NotifyAsync("0.138.0", "0.137.1", default);

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        // Ни личных, ни общесистемных: некому сообщать — значит молчим, а не рассылаем всем подряд.
        Assert.Empty(await db.Notifications.AsNoTracking()
            .Where(n => n.Source == UpdateNotifier.Source).ToListAsync());
        Assert.Empty((await check.ServiceProvider.GetRequiredService<INotificationService>()
            .GetAsync(userId)).Where(n => n.Source == UpdateNotifier.Source));
    }

    // ── Решение «сообщать или нет» ───────────────────────────────────────────────

    [Theory]
    [InlineData("0.138.0", "0.137.1", null, true)]              // вышла новее — сообщаем
    [InlineData("0.138.0", "0.137.1", "0.138.0", false)]        // об этой уже сообщали
    [InlineData("0.138.0", "0.137.1", "0.137.5", true)]         // сообщали о другой — эта новая
    [InlineData("0.137.1", "0.137.1", null, false)]             // та же версия
    [InlineData("0.137.0", "0.137.1", null, false)]             // выпуск старше установленной
    [InlineData(null, "0.137.1", null, false)]                  // ещё ничего не знаем
    public void ShouldNotify_OncePerVersion(string? latest, string installed, string? notified, bool expected)
        => Assert.Equal(expected, UpdateNotifier.ShouldNotify(latest, installed, notified));

    [Fact]
    public void ShouldNotify_IgnoresTagWrapper()
    {
        // Состояние хранит то, что пришло от GitHub («v0.138.0»), а сравнение обязано понимать обе
        // формы — иначе после перезапуска уведомление повторилось бы о той же версии.
        Assert.False(UpdateNotifier.ShouldNotify("v0.138.0", "0.137.1", "v0.138.0"));
        Assert.True(UpdateNotifier.ShouldNotify("v0.138.0", "0.137.1", "v0.137.9"));
    }
}
