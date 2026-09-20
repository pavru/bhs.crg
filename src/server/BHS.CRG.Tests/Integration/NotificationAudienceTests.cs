using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Аудитория уведомлений считается по правам (issue #949, ТЗ AUTH-13, CORE-27).
///
/// До этого способов адресации было два, и оба мимо прав: «всем вошедшим» (<c>UserId == null</c>) и
/// перебор пользователей РОЛИ <c>Admin</c> с личной копией каждому. Первый приносил дела чужого
/// модуля тому, у кого модуля нет; второй решал по имени роли — ровно то, что права отменяют, — и
/// замораживал список получателей в момент публикации.
///
/// ⚠️ Проверяется ДВУСТОРОННЕ: что получатель уведомление видит И что посторонний не видит. Одна
/// половина без другой проходит и на сломанном отборе: «видит всё» и «не видит ничего» выглядят
/// работающими, пока смотришь одним глазом.
/// </summary>
[Collection("Integration")]
public class NotificationAudienceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Password = "Passw0rd!";

    private static INotificationService Service(IServiceScope s)
        => s.ServiceProvider.GetRequiredService<INotificationService>();

    /// <summary>Пользователь с ролью; без роли прав нет вовсе, и проверять было бы нечего.</summary>
    private async Task<Guid> UserAsync(string? role)
    {
        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"aud_{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        if (role is not null) Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        return user.Id;
    }

    private async Task GrantAsync(Guid userId, string role)
    {
        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId.ToString());
        Assert.True((await users.AddToRoleAsync(user!, role)).Succeeded);
    }

    private async Task<Guid> PublishAsync(string title, string? audience)
    {
        using var scope = fixture.Services.CreateScope();
        await Service(scope).PublishAsync(NotificationSeverity.Info, title, "текст", "Тест", audience: audience);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Notifications.Where(n => n.Title == title)
            .OrderByDescending(n => n.CreatedAt).Select(n => n.Id).FirstAsync();
    }

    private async Task<bool> SeesAsync(Guid userId, Guid notificationId)
    {
        using var scope = fixture.Services.CreateScope();
        return (await Service(scope).GetAsync(userId, take: 300)).Any(n => n.Id == notificationId);
    }

    /// <summary>
    /// То самое «Готово» из issue: уведомление модуля не приходит тому, у кого доступа к модулю нет.
    /// Модуль счетов на этом экземпляре не включён, поэтому берём тот, что есть, — исполнительную
    /// документацию: у инженера ИД её права есть, у бухгалтера нет ни одного.
    /// </summary>
    [Fact]
    public async Task Уведомление_модуля_не_приходит_тому_у_кого_нет_доступа_к_модулю()
    {
        var engineer = await UserAsync(SystemRoles.IdEngineer);
        var accountant = await UserAsync("Accountant");

        var id = await PublishAsync("Дела модуля ИД", "id");

        Assert.True(await SeesAsync(engineer, id));
        Assert.False(await SeesAsync(accountant, id));
    }

    /// <summary>Аудитория правом: обслуживание системы — дело не инженера.</summary>
    [Fact]
    public async Task Уведомление_обслуживания_не_приходит_тому_кто_систему_не_обслуживает()
    {
        var admin = await UserAsync(SystemRoles.Admin);
        var engineer = await UserAsync(SystemRoles.IdEngineer);

        var id = await PublishAsync("Доступна версия", NotificationAudiences.SystemManage);

        Assert.True(await SeesAsync(admin, id));
        Assert.False(await SeesAsync(engineer, id));
    }

    /// <summary>
    /// Без аудитории — по-прежнему всем вошедшим. Это не забытое умолчание: так адресуется
    /// состояние системы, которое видно всем и на панели (решение владельца 20.09.2026).
    /// </summary>
    [Fact]
    public async Task Без_аудитории_уведомление_приходит_всем_вошедшим()
    {
        var engineer = await UserAsync(SystemRoles.IdEngineer);
        var accountant = await UserAsync("Accountant");

        var id = await PublishAsync("Компонент восстановлен", null);

        Assert.True(await SeesAsync(engineer, id));
        Assert.True(await SeesAsync(accountant, id));
    }

    /// <summary>
    /// Счётчик колокольчика считает по той же аудитории, что и список. Проверяется отдельно, потому
    /// что это ДРУГОЙ запрос: пропустив в нём отбор, получаем «непрочитанных 3» и пустой список —
    /// красную точку, которую нечем погасить.
    /// </summary>
    [Fact]
    public async Task Счётчик_непрочитанного_считает_по_той_же_аудитории()
    {
        var engineer = await UserAsync(SystemRoles.IdEngineer);
        await PublishAsync("Доступна версия", NotificationAudiences.SystemManage);

        using var scope = fixture.Services.CreateScope();
        var unread = await Service(scope).UnreadCountAsync(engineer);
        var list = await Service(scope).GetAsync(engineer, unreadOnly: true, take: 300);

        Assert.Equal(list.Count, unread);
        Assert.DoesNotContain(list, n => n.Title == "Доступна версия");
    }

    /// <summary>
    /// Аудитория проверяется ПРИ ЧТЕНИИ, а не при публикации.
    ///
    /// Это и есть отличие от прежнего перебора администраторов: там получатели замерзали в момент
    /// выпуска, и заведённый назавтра администратор не узнавал ничего — «для системы уже
    /// отправлено». Здесь право выдано после публикации, и уведомление появляется.
    ///
    /// ⚠️ Отрицательная половина взята с ДРУГОГО инженера нарочно. Посчитанные ключи живут в кэше
    /// <c>PermissionAudience.Lifetime</c> (30 с), и прочитать список тем же человеком до выдачи
    /// права значило бы сохранить его старые ключи на всю оставшуюся часть теста: проверка упала бы
    /// не на отборе, а на кэше — и читалась бы как «право не подействовало».
    /// </summary>
    [Fact]
    public async Task Аудитория_считается_при_чтении_а_не_при_публикации()
    {
        var newcomer = await UserAsync(SystemRoles.IdEngineer);
        var engineer = await UserAsync(SystemRoles.IdEngineer);

        var id = await PublishAsync("Доступна версия", NotificationAudiences.SystemManage);
        Assert.False(await SeesAsync(engineer, id));

        await GrantAsync(newcomer, SystemRoles.Admin);

        // Уведомление выпущено ДО того, как право появилось, — и всё равно видно.
        Assert.True(await SeesAsync(newcomer, id));
    }

    /// <summary>
    /// Опечатка в коде адресует уведомление НИКОМУ, и выглядит это как удачная отправка: запись
    /// создана, в списке её ни у кого нет. Поэтому отказ на публикации (та же мысль, что у ворот на
    /// необъявленное право, AUTH-8.2).
    /// </summary>
    [Fact]
    public async Task Несуществующая_аудитория_отказ_на_публикации()
    {
        using var scope = fixture.Services.CreateScope();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Service(scope).PublishAsync(
            NotificationSeverity.Info, "Кому-то", "текст", "Тест", audience: "core.системы.управление"));

        Assert.Contains("core.системы.управление", ex.Message);
    }

    /// <summary>Лично И по праву разом — путаница в издателе, а не сужение адресата.</summary>
    [Fact]
    public async Task Личный_адресат_и_аудитория_вместе_отказ()
    {
        var user = await UserAsync(SystemRoles.Admin);
        using var scope = fixture.Services.CreateScope();

        await Assert.ThrowsAsync<ArgumentException>(() => Service(scope).PublishAsync(
            NotificationSeverity.Info, "И то и другое", "текст", "Тест", userId: user,
            audience: NotificationAudiences.SystemManage));
    }

    /// <summary>
    /// Отбор стоит не только в списке: чужое уведомление нельзя пометить прочитанным по прямому id.
    /// Иначе отбор был бы украшением экрана — адрес отвечал бы на чужую запись согласием.
    /// </summary>
    [Fact]
    public async Task Чужое_по_аудитории_нельзя_пометить_прочитанным()
    {
        var engineer = await UserAsync(SystemRoles.IdEngineer);
        var id = await PublishAsync("Доступна версия", NotificationAudiences.SystemManage);

        using var scope = fixture.Services.CreateScope();
        await Service(scope).MarkReadAsync(id, engineer);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.NotificationUserStates.AnyAsync(s => s.NotificationId == id && s.UserId == engineer));
    }
}
