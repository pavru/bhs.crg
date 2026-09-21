using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Несколько ролей у одного человека (ТЗ AUTH-3, issue #984).
///
/// ⚠️ Проверяется ЧЕРЕЗ ЖИВОЙ ВХОД, а не чтением состава из базы. Объединение прав считает сервер
/// при каждой проверке, и тест, сверяющий строки в таблице связей, подтвердил бы только то, что
/// связи записались. Вопрос же другой: открылась ли дверь.
///
/// Двери взяты РАЗНЫЕ и у каждой роли своя — иначе «обе роли работают» нельзя отличить от «работает
/// одна из двух».
/// </summary>
[Collection("Integration")]
public class UserRolesTests(IntegrationTestFixture fixture)
{
    private const string Password = "Passw0rd!";

    /// <summary>Дверь права «читать журнал действий».</summary>
    private const string JournalDoor = "/api/activity";

    /// <summary>Дверь права «вести сверки».</summary>
    private const string ReconciliationDoor = "/api/reconciliations";

    /// <summary>
    /// «Готово» из issue: у человека с двумя ролями открыты двери обеих, а снятие одной роли не
    /// закрывает двери второй.
    /// </summary>
    [Fact]
    public async Task Две_роли_открывают_двери_обеих_а_снятие_одной_не_трогает_вторую()
    {
        var (admin, _) = await SignInAsync(SystemRoles.Admin);
        var journalRole = await CreateRoleAsync(admin, "Журнальщик", CorePermissions.AuditRead);
        var reconRole = await CreateRoleAsync(admin, "Сверщик", CorePermissions.ReconciliationRun);

        var (user, userId, email) = await CreateUserAsync(admin, [journalRole, reconRole]);

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync(JournalDoor)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync(ReconciliationDoor)).StatusCode);

        (await admin.PutAsJsonAsync($"/api/users/{userId}/roles", new { roles = new[] { reconRole.Name } }))
            .EnsureSuccessStatusCode();

        // ⚠️ Смена ролей обнуляет отметку безопасности (AUTH-7), и ПРЕЖНИЙ токен перестаёт
        // приниматься целиком — оба адреса ответили бы 401. Это верное поведение и не то, что
        // проверяется здесь: в работе клиент молча меняет токен по refresh. Берём свежий — иначе
        // «дверь закрылась» не отличить от «токен устарел».
        user = await ClientForAsync(email);

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync(ReconciliationDoor)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync(JournalDoor)).StatusCode);
    }

    /// <summary>
    /// Список пользователей отдаёт ВСЕ роли, а не первую. Показанная первая означала бы, что экран
    /// называет часть выданного доступа целым, — и «лишнюю» роль сняли бы, не зная о ней.
    /// </summary>
    [Fact]
    public async Task Список_пользователей_отдаёт_все_роли()
    {
        var (admin, _) = await SignInAsync(SystemRoles.Admin);
        var first = await CreateRoleAsync(admin, "Первая", CorePermissions.AuditRead);
        var second = await CreateRoleAsync(admin, "Вторая", CorePermissions.ReconciliationRun);
        var (_, userId, _) = await CreateUserAsync(admin, [first, second]);

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/users");
        var row = list.EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == userId);
        var names = row.GetProperty("roles").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).ToList();

        Assert.Equal(2, names.Count);
        Assert.Contains(first.Name, names);
        Assert.Contains(second.Name, names);
        // Название рядом с именем: по имени вида role-1a2b3c4d экран подписать роль не может.
        Assert.Equal(first.Title, row.GetProperty("roles").EnumerateArray()
            .Single(r => r.GetProperty("name").GetString() == first.Name).GetProperty("title").GetString());
    }

    /// <summary>
    /// Снять все роли можно — это отзыв доступа без удаления учётной записи. Человек входит, и не
    /// видит ничего: пустой набор ролей означает пустой набор прав, а не «права как были».
    /// </summary>
    [Fact]
    public async Task Пустой_набор_ролей_отзывает_доступ_но_не_учётную_запись()
    {
        var (admin, _) = await SignInAsync(SystemRoles.Admin);
        var role = await CreateRoleAsync(admin, "Временная", CorePermissions.AuditRead);
        var (user, userId, email) = await CreateUserAsync(admin, [role]);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync(JournalDoor)).StatusCode);

        (await admin.PutAsJsonAsync($"/api/users/{userId}/roles", new { roles = Array.Empty<string>() }))
            .EnsureSuccessStatusCode();

        // Вход остался — учётная запись цела, и это половина утверждения: отозван ДОСТУП, а не
        // возможность войти. Токен свежий, а дверь всё равно закрыта.
        var fresh = await ClientForAsync(email);
        Assert.Equal(HttpStatusCode.Forbidden, (await fresh.GetAsync(JournalDoor)).StatusCode);
    }

    /// <summary>
    /// ЗАВЕДЕНИЕ без ролей, наоборот, отвергается: человек без единой роли войдёт и не увидит
    /// ничего, а выглядеть это будет как поломка доступа, а не как решение администратора.
    /// </summary>
    [Fact]
    public async Task Завести_пользователя_без_ролей_нельзя()
    {
        var (admin, _) = await SignInAsync(SystemRoles.Admin);

        var created = await admin.PostAsJsonAsync("/api/users", new
        {
            email = $"noroles_{Guid.NewGuid():N}@test.local",
            displayName = "Без ролей", password = Password, roles = Array.Empty<string>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    }

    /// <summary>
    /// Опечатка в одном имени отвергает ВЕСЬ запрос. Пропусти мы неизвестную роль молча — человек
    /// получил бы две роли из трёх и ответ «готово»: доступ уже, чем показала форма, и заметится
    /// это тогда, когда он не сможет что-то сделать.
    /// </summary>
    [Fact]
    public async Task Неизвестная_роль_в_списке_отвергает_весь_запрос()
    {
        var (admin, _) = await SignInAsync(SystemRoles.Admin);
        var role = await CreateRoleAsync(admin, "Настоящая", CorePermissions.AuditRead);
        var (user, userId, _) = await CreateUserAsync(admin, [role]);

        var answer = await admin.PutAsJsonAsync($"/api/users/{userId}/roles",
            new { roles = new[] { role.Name, "role-которой-нет" } });

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        // И прежние роли на месте: отказ не оставил человека ни с чем.
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync(JournalDoor)).StatusCode);
    }

    /// <summary>
    /// Защита от самоограничения считается ПО ПРАВУ, а не по имени роли «Admin» (ТЗ AUTH-8.2).
    ///
    /// Роль с правом управлять пользователями администратор вправе завести и назвать как угодно;
    /// проверка по имени такую роль не увидела бы вовсе — и отобрала бы у экземпляра управление,
    /// формально «пройдя».
    /// </summary>
    [Fact]
    public async Task Нельзя_снять_с_себя_право_управлять_пользователями()
    {
        var (admin, me) = await SignInAsync(SystemRoles.Admin);
        var myId = await IdOfAsync(admin, me);

        var harmless = await CreateRoleAsync(admin, "Безобидная", CorePermissions.AuditRead);

        var answer = await admin.PutAsJsonAsync($"/api/users/{myId}/roles", new { roles = new[] { harmless.Name } });

        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/users")).StatusCode);
    }

    /// <summary>
    /// А вот СЕБЕ добавить роль сверх управления пользователями — можно: право остаётся, и
    /// запрещать тут нечего. Без этой проверки защита выше читалась бы как «администратор не
    /// может менять себе роли вовсе».
    /// </summary>
    [Fact]
    public async Task Себе_можно_добавить_роль_пока_право_управления_остаётся()
    {
        var (admin, me) = await SignInAsync(SystemRoles.Admin);
        var myId = await IdOfAsync(admin, me);

        var extra = await CreateRoleAsync(admin, "Сверщик себе", CorePermissions.ReconciliationRun);

        (await admin.PutAsJsonAsync($"/api/users/{myId}/roles",
            new { roles = new[] { SystemRoles.Admin, extra.Name } })).EnsureSuccessStatusCode();

        // Свой же токен обнулился сменой своих ролей — берём свежий (см. пояснение выше).
        var profile = await (await ClientForAsync(me)).GetFromJsonAsync<JsonElement>("/api/account");
        Assert.Equal(2, profile.GetProperty("roles").GetArrayLength());
    }

    /// <summary>Профиль отдаёт все роли — их показывает боковая панель под именем.</summary>
    [Fact]
    public async Task Профиль_отдаёт_все_роли()
    {
        var (admin, _) = await SignInAsync(SystemRoles.Admin);
        var first = await CreateRoleAsync(admin, "Профильная А", CorePermissions.AuditRead);
        var second = await CreateRoleAsync(admin, "Профильная Б", CorePermissions.ReconciliationRun);
        var (user, _, _) = await CreateUserAsync(admin, [first, second]);

        var profile = await user.GetFromJsonAsync<JsonElement>("/api/account");
        var titles = profile.GetProperty("roles").EnumerateArray()
            .Select(r => r.GetProperty("title").GetString()).ToList();

        Assert.Equal([first.Title, second.Title], titles.Order(StringComparer.CurrentCulture).ToList());
    }

    /// <summary>
    /// Заводит роль с одним правом. Название уникально — роли живут в общей базе, и два прогона с
    /// одинаковым названием встретились бы отказом «такая роль уже есть».
    /// </summary>
    private static async Task<Role> CreateRoleAsync(HttpClient admin, string title, string permission)
    {
        var unique = $"{title} {Guid.NewGuid():N}"[..20];
        var created = await admin.PostAsJsonAsync("/api/roles",
            new { title = unique, permissions = new[] { permission } });
        created.EnsureSuccessStatusCode();
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        return new Role(body.GetProperty("name").GetString()!, body.GetProperty("title").GetString()!);
    }

    /// <summary>Заведённая роль: техническое имя — чтобы назначить, название — чтобы сверить.</summary>
    private record Role(string Name, string Title);

    /// <summary>Заводит пользователя через живой адрес и возвращает клиент с его токеном.</summary>
    private async Task<(HttpClient Client, Guid Id, string Email)> CreateUserAsync(
        HttpClient admin, Role[] roles)
    {
        var email = $"multi_{Guid.NewGuid():N}@test.local";
        var created = await admin.PostAsJsonAsync("/api/users", new
        {
            email, displayName = "Тест", password = Password, roles = roles.Select(r => r.Name),
        });
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        return (await ClientForAsync(email), id, email);
    }

    private async Task<(HttpClient Client, string Email)> SignInAsync(string role)
    {
        var email = $"{role.ToLowerInvariant()}_{Guid.NewGuid():N}@test.local";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }
        return (await ClientForAsync(email), email);
    }

    /// <summary>
    /// Сторож находки ревью #984: снять право управления НЕЛЬЗЯ и на экране ролей.
    ///
    /// До #984 этот путь был закрыт сам собой: право жило только у роли «все права», а её состав
    /// не правится вовсе. Проверка «по праву, а не по имени роли» открыла обход: единственный
    /// администратор уходит из «Администратора» в свою роль с этим правом — и снимает галку там.
    /// Защита у пользователей такого хода не видит: состав ролей она не меняла.
    /// </summary>
    [Fact]
    public async Task Снять_право_управления_с_единственной_управляющей_роли_нельзя()
    {
        // Чужие администраторы прошлых классов сделали бы «кроме него есть кто-то ещё» истинным, и
        // проверка прошла бы, не коснувшись защиты. Учётные записи фикстура не чистит — чистим сами.
        await ClearUsersAsync();

        var (admin, me) = await SignInAsync(SystemRoles.Admin);
        var manager = await CreateRoleAsync(admin, "Управляющий", CorePermissions.UsersManage);

        // Единственный администратор уходит в свою роль: право остаётся при нём, и это разрешено.
        var myId = await IdOfAsync(admin, me);
        (await admin.PutAsJsonAsync($"/api/users/{myId}/roles", new { roles = new[] { manager.Name } }))
            .EnsureSuccessStatusCode();
        admin = await ClientForAsync(me);   // смена своих ролей обнулила прежний токен

        var stripped = await admin.PutAsJsonAsync($"/api/roles/{manager.Name}/permissions",
            new { permissions = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.Conflict, stripped.StatusCode);
        Assert.Contains(CorePermissions.UsersManage, await stripped.Content.ReadAsStringAsync());

        // И управление на месте — отказ не оставил экземпляр в половинчатом состоянии.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/users")).StatusCode);
    }

    /// <summary>
    /// Обратное тоже верно: пока право есть у кого-то ещё, снять его с роли можно. Без этой
    /// проверки защита выше читалась бы как «право управления не снимается никогда».
    /// </summary>
    [Fact]
    public async Task Снять_право_управления_можно_пока_оно_есть_у_кого_то_ещё()
    {
        var (admin, _) = await SignInAsync(SystemRoles.Admin);
        var manager = await CreateRoleAsync(admin, "Второй управляющий", CorePermissions.UsersManage);
        await CreateUserAsync(admin, [manager]);   // носитель у роли есть

        var stripped = await admin.PutAsJsonAsync($"/api/roles/{manager.Name}/permissions",
            new { permissions = Array.Empty<string>() });

        // Админ с ролью «все права» на месте, и он — тот самый «кто-то ещё».
        stripped.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Убирает все учётные записи: иначе «кроме него никто не управляет» не проверить — чужие
    /// администраторы прошлых классов остаются в базе (фикстура их не чистит, см.
    /// <c>FixtureResetCoverageTests</c>).
    /// </summary>
    private async Task ClearUsersAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        foreach (var u in await users.Users.ToListAsync())
            Assert.True((await users.DeleteAsync(u)).Succeeded);
    }

    /// <summary>Идентификатор пользователя по почте — через тот же список, что видит экран.</summary>
    private static async Task<Guid> IdOfAsync(HttpClient admin, string email)
    {
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/users");
        return list.EnumerateArray()
            .Single(u => u.GetProperty("email").GetString() == email).GetProperty("id").GetGuid();
    }

    private async Task<HttpClient> ClientForAsync(string email)
    {
        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
