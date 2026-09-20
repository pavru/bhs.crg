using BHS.CRG.Application.Notifications;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Infrastructure.Updates;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Кому достаётся сообщение о новой версии (issue #813, адресация по правам — issue #949).
///
/// Проверяется здесь, а не на глаз, потому что «пришло всем» и «пришло кому надо» выглядят
/// одинаково, пока смотришь со стороны администратора: у него есть все права, и любая ошибка
/// отбора видна ему как успех.
///
/// ⚠️ Прежде здесь проверялась ЛИЧНАЯ копия каждому администратору — так пришлось делать, пока
/// «прочитано» лежало на самой записи и первый прочитавший гасил её у всех (issue #821). Состояние
/// давно у каждого своё, и адресация переведена на право <c>core.system.manage</c>: одна запись,
/// получатели считаются при чтении.
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
    public async Task Сообщение_об_обновлении_видит_тот_кто_обслуживает_систему()
    {
        var adminId = await CreateUserAsync("Администратор", "Admin");
        var userId = await CreateUserAsync("Пользователь", "User");
        await KeepOnlyAdminsAsync(adminId);

        using (var scope = fixture.Services.CreateScope())
            await Notifier(scope).NotifyAsync("0.138.0", "0.137.1", default);

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();

        // Запись ОДНА, с названной аудиторией — а не копия на каждого администратора.
        var sent = Assert.Single(await db.Notifications.AsNoTracking()
            .Where(n => n.Source == UpdateNotifier.Source).ToListAsync());
        Assert.Contains("0.138.0", sent.Title);
        Assert.Contains("0.137.1", sent.Message);
        Assert.Null(sent.UserId);
        Assert.Equal("core.system.manage", sent.Audience);

        // Ключевое — двусторонне: обслуживающий видит, а инженеру сообщение не адресовано. Номер
        // версии он и так видит пассивно, в подвале боковой панели.
        var notifier = check.ServiceProvider.GetRequiredService<INotificationService>();
        Assert.Single(await notifier.GetAsync(adminId), n => n.Source == UpdateNotifier.Source);
        Assert.DoesNotContain(await notifier.GetAsync(userId), n => n.Source == UpdateNotifier.Source);
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
            .Where(n => n.Source == UpdateNotifier.Source).ToListAsync();

        // К третьему выпуску в колокольчике лежали бы три записи об одном и том же, и свежая
        // терялась бы среди устаревших.
        var one = Assert.Single(sent);
        Assert.Contains("0.139.0", one.Title);
    }

    [Fact]
    public async Task Без_права_на_обслуживание_сообщение_не_видно_никому()
    {
        var userId = await CreateUserAsync("Пользователь", "User");
        await KeepOnlyAdminsAsync();   // администраторов не осталось вовсе

        using (var scope = fixture.Services.CreateScope())
            await Notifier(scope).NotifyAsync("0.138.0", "0.137.1", default);

        using var check = fixture.Services.CreateScope();
        // Запись появляется — но получателей у неё нет, и инженер её не видит. Это отличие от
        // прежнего поведения («нет администраторов — не публикуем»), и оно осознанное: выдайте
        // право завтра, и сообщение найдёт человека, а не потеряется в дне, когда его выпустили.
        Assert.DoesNotContain(
            await check.ServiceProvider.GetRequiredService<INotificationService>().GetAsync(userId),
            n => n.Source == UpdateNotifier.Source);
    }

    [Fact]
    public async Task AfterUpdate_ClearsStaleMessage()
    {
        // Обновились до объявленной версии — сообщение «доступна 0.139.0» стало неправдой. Без
        // очистки оно висит до СЛЕДУЮЩЕГО выпуска: сообщение о выполненной работе — тот же мусор,
        // что и лампа, горящая всегда, только с виду осмысленный.
        var adminId = await CreateUserAsync("Администратор", "Admin");
        await KeepOnlyAdminsAsync(adminId);

        using (var scope = fixture.Services.CreateScope())
        {
            await Notifier(scope).NotifyAsync("0.139.0", "0.138.0", default);
            await Notifier(scope).ClearAsync(default);
        }

        using var check = fixture.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Notifications.AsNoTracking()
            .Where(n => n.Source == UpdateNotifier.Source).ToListAsync());
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
