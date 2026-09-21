using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Редактор матрицы ролей (issue #951, ТЗ AUTH-5, AUTH-5.1).
///
/// ⚠️ Всё проверяется через живые адреса и живой вход. Правка состава прав ломается ровно там, где
/// её удобнее всего проверять в обход: у пользователя, который УЖЕ вошёл. Вызов редактора из теста
/// с последующим чтением базы показал бы, что состав записан, — и ничего не сказал бы о том,
/// подействовал ли он.
/// </summary>
[Collection("Integration")]
public class RoleEditorTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    /// <summary>
    /// ⚠️ Роли фикстура НЕ чистит: <c>AspNetRoles</c> сознательно вне списка очистки — системные
    /// роли создаёт приложение при старте, и TRUNCATE их не вернёт. Значит, за собой обязаны
    /// прибирать мы, и с обеих сторон: правка состава СИСТЕМНОЙ роли переживает перезапуск (в том и
    /// смысл задачи), то есть оставленная — переживёт и весь остальной прогон. Роль «Инженер ИД» с
    /// одним правом ломала бы чужие тесты там, где про роли не сказано ни слова.
    /// </summary>
    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        await RestoreDeclaredRolesAsync();
    }

    public Task DisposeAsync() => RestoreDeclaredRolesAsync();

    private const string Password = "Passw0rd!";

    /// <summary>
    /// Название роли с хвостом: роли живут дольше одного теста, а редактор отклоняет повтор
    /// названия. Без хвоста второй прогон падал бы на «роль уже есть» — и читалось бы это как
    /// поломка редактора, а не как след прошлого прогона.
    /// </summary>
    private static string Title(string prefix) => $"{prefix} {Guid.NewGuid():N}"[..(prefix.Length + 7)];

    /// <summary>
    /// «Готово» из issue: администратор выдаёт ровно одно право — и открывается ровно одна дверь.
    ///
    /// Проверяется ДВУСТОРОННЕ. Одна половина проходит и на сломанном редакторе: роль, которой
    /// выдали всё, откроет и нужную дверь тоже, а роль, которой не выдали ничего, «правильно» не
    /// откроет соседнюю.
    /// </summary>
    [Fact]
    public async Task Роль_с_одним_правом_открывает_ровно_одну_дверь()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var role = await CreateRoleAsync(admin, Title("Разбор обращений"), [CorePermissions.SupportReview]);
        var clerk = await SignInWithRoleAsync(role);

        // Выданное право работает…
        Assert.Equal(HttpStatusCode.OK, (await clerk.GetAsync("/api/bug-reports")).StatusCode);

        // …и ничего лишнего с ним не пришло: ни пользователей, ни журнала, ни настроек.
        foreach (var closed in new[] { "/api/users", "/api/activity", "/api/roles" })
            Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync(closed)).StatusCode);
    }

    /// <summary>
    /// Сторож из issue: правка состава прав действует немедленно у того, кто уже вошёл.
    ///
    /// ⚠️ Это и есть проверка сброса кэша прав. Посчитанный набор живёт 30 секунд
    /// (<c>PermissionCache.Lifetime</c>), и без обновления отметки безопасности у носителей роли
    /// человек продолжил бы ходить со старыми правами — тест падает именно на этом, а не на
    /// содержимом базы.
    /// </summary>
    [Fact]
    public async Task Снятое_у_роли_право_закрывает_дверь_немедленно()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var role = await CreateRoleAsync(admin, Title("Временный разбор"), [CorePermissions.SupportReview]);
        var clerk = await SignInWithRoleAsync(role);

        // Дверь открыта — иначе проверка ниже ничего не значила бы. Заодно набор прав попадает в
        // кэш: дальше проверяется, что он перестал действовать, а не что его там не было.
        Assert.Equal(HttpStatusCode.OK, (await clerk.GetAsync("/api/bug-reports")).StatusCode);

        (await admin.PutAsJsonAsync($"/api/roles/{role}/permissions", new { permissions = Array.Empty<string>() }))
            .EnsureSuccessStatusCode();

        // Тот же токен, следующий запрос — и ждать тридцати секунд не пришлось.
        var after = await clerk.GetAsync("/api/bug-reports");
        Assert.True(after.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Снятое у роли право не подействовало: ответ {(int)after.StatusCode}. " +
            "Похоже, посчитанный набор прав остался в силе.");
    }

    /// <summary>Вторая половина сторожа: правка состава прав попадает в журнал (ТЗ AUTH-5.1).</summary>
    [Fact]
    public async Task Правка_состава_прав_пишется_в_журнал()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        var title = Title("Кладовщик");
        var role = await CreateRoleAsync(admin, title, [CorePermissions.CatalogRead]);

        (await admin.PutAsJsonAsync($"/api/roles/{role}/permissions",
            new { permissions = new[] { CorePermissions.CatalogRead, CorePermissions.FilesUse } }))
            .EnsureSuccessStatusCode();

        var record = Assert.Single(await RecordsAsync(ActivityActions.RolePermissionsChanged));
        Assert.Equal(title, record.TargetLabel);
        Assert.Equal($"права: {CorePermissions.CatalogRead}", record.Before);
        Assert.Contains($"выдано: {CorePermissions.FilesUse}", record.After);

        // Заведение роли — тоже выдача прав, и оно в журнале своё.
        Assert.Single(await RecordsAsync(ActivityActions.RoleCreated));
    }

    /// <summary>
    /// «Администратора» нельзя лишить управления пользователями (ТЗ AUTH-5): иначе экземпляр
    /// остаётся без единого человека, способного это исправить.
    /// </summary>
    [Fact]
    public async Task Администратор_не_может_лишиться_управления_пользователями()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var refused = await admin.PutAsJsonAsync($"/api/roles/{SystemRoles.Admin}/permissions",
            new { permissions = new[] { CorePermissions.CatalogRead } });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(CorePermissions.UsersManage, await refused.Content.ReadAsStringAsync());

        // И право осталось на месте: отказ обязан быть отказом, а не половиной правки.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/users")).StatusCode);
    }

    /// <summary>Системную роль правят, но не удаляют (ТЗ AUTH-4, AUTH-5).</summary>
    [Fact]
    public async Task Системную_роль_нельзя_удалить_а_состав_прав_ей_менять_можно()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var refused = await admin.DeleteAsync($"/api/roles/{SystemRoles.IdEngineer}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var edited = await admin.PutAsJsonAsync($"/api/roles/{SystemRoles.IdEngineer}/permissions",
            new { permissions = new[] { CorePermissions.CatalogRead } });
        edited.EnsureSuccessStatusCode();

        var view = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(view.GetProperty("system").GetBoolean());
        Assert.True(view.GetProperty("edited").GetBoolean());
    }

    /// <summary>
    /// Правленый состав системной роли переживает перезапуск.
    ///
    /// ⚠️ Это главный подвох задачи: синхронизатор при старте ПРИВОДИТ состав системной роли к
    /// объявленному в коде. Не отметь мы роль как правленую — администратор снял бы право, а
    /// следующий запуск вернул бы его. Отменённая правка без единого сообщения выглядит как
    /// «система сама раздаёт доступ».
    /// </summary>
    [Fact]
    public async Task Правленый_состав_системной_роли_переживает_перезапуск()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        (await admin.PutAsJsonAsync($"/api/roles/{SystemRoles.IdEngineer}/permissions",
            new { permissions = new[] { CorePermissions.CatalogRead } })).EnsureSuccessStatusCode();

        await SynchronizeRolesAsync();

        var after = await PermissionsOfAsync(admin, SystemRoles.IdEngineer);
        Assert.Equal([CorePermissions.CatalogRead], after);
    }

    /// <summary>
    /// А вот управление пользователями «Администратору» возвращается даже у правленой роли: это
    /// последняя гарантия, и она не про то, кто владеет составом, а про то, что экземпляр остаётся
    /// управляемым. Снять его можно только в обход приложения — прямо в базе.
    /// </summary>
    [Fact]
    public async Task Управление_пользователями_возвращается_администратору_при_старте()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        // Отмечаем роль правленой честным путём — через редактор; состав при этом не меняем.
        (await admin.PutAsJsonAsync($"/api/roles/{SystemRoles.Admin}/permissions",
            new { permissions = await PermissionsOfAsync(admin, SystemRoles.Admin) })).EnsureSuccessStatusCode();

        // Право снимаем мимо приложения: редактор такого не позволит, а правка в базе — да.
        using (var scope = fixture.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var role = (await roles.FindByNameAsync(SystemRoles.Admin))!;
            await roles.RemoveClaimAsync(role,
                new System.Security.Claims.Claim(RoleSynchronizer.PermissionClaim, CorePermissions.UsersManage));
        }

        await SynchronizeRolesAsync();

        Assert.Contains(CorePermissions.UsersManage, await PermissionsOfAsync(admin, SystemRoles.Admin));
    }

    /// <summary>
    /// Несуществующее право выдать нельзя. Галка с опечаткой не открывает ни одной двери, а
    /// выглядит выданной — тот же случай, что и несуществующая аудитория уведомления (issue #949).
    /// </summary>
    [Fact]
    public async Task Несуществующее_право_выдать_нельзя()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var refused = await admin.PostAsJsonAsync("/api/roles", new
        {
            title = Title("Роль с опечаткой"),
            permissions = new[] { "core.юзеры.manage" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("core.юзеры.manage", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Права приходят сгруппированными по модулям и с объяснениями — условие, без которого
    /// редактор небезопасен (ТЗ AUTH-5): администратор раздаёт галки, а не читает коды.
    /// </summary>
    [Fact]
    public async Task Права_приходят_по_модулям_и_с_объяснениями()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var groups = await admin.GetFromJsonAsync<JsonElement>("/api/roles/permissions");
        var all = groups.EnumerateArray().ToList();

        Assert.Equal("core", all[0].GetProperty("module").GetString());   // ядро первым
        Assert.Contains(all, g => g.GetProperty("module").GetString() == "id");

        foreach (var permission in all.SelectMany(g => g.GetProperty("permissions").EnumerateArray()))
        {
            Assert.False(string.IsNullOrWhiteSpace(permission.GetProperty("gives").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(permission.GetProperty("opens").GetString()));
        }
    }

    /// <summary>Роль носят люди — удаление забрало бы доступ у всех разом и без следа, кому.</summary>
    [Fact]
    public async Task Роль_которую_носят_удалить_нельзя()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        var role = await CreateRoleAsync(admin, Title("Заведующий"), [CorePermissions.CatalogRead]);
        await SignInWithRoleAsync(role);

        var refused = await admin.DeleteAsync($"/api/roles/{role}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("носят", await refused.Content.ReadAsStringAsync());
    }

    /// <summary>Редактор ролей закрыт тем же правом, что и пользователи (ТЗ AUTH-8).</summary>
    [Fact]
    public async Task Редактор_ролей_закрыт_правом()
    {
        var engineer = await SignInAsync(SystemRoles.IdEngineer);

        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync("/api/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await engineer.PostAsJsonAsync("/api/roles", new { title = "Своя роль" })).StatusCode);
    }

    // ── Вспомогательное ───────────────────────────────────────────────────────

    /// <summary>Заводит роль через живой адрес и возвращает её техническое имя.</summary>
    private static async Task<string> CreateRoleAsync(HttpClient admin, string title, string[] permissions)
    {
        var created = await admin.PostAsJsonAsync("/api/roles", new { title, permissions });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString()!;
    }

    private static async Task<string[]> PermissionsOfAsync(HttpClient admin, string role)
    {
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/roles");
        return [.. list.EnumerateArray()
            .First(r => r.GetProperty("name").GetString() == role)
            .GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetString()!)];
    }

    /// <summary>
    /// Возвращает системные роли к объявленному составу: снимаем отметку «правил администратор» и
    /// зовём синхронизатор. Именно эта отметка и делает правку живучей — без её снятия уборка
    /// ничего не убрала бы.
    /// </summary>
    private async Task RestoreDeclaredRolesAsync()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            foreach (var definition in SystemRoles.All)
            {
                var role = await roles.FindByNameAsync(definition.Name);
                if (role is null) continue;
                foreach (var mark in (await roles.GetClaimsAsync(role))
                         .Where(c => c.Type == RoleSynchronizer.EditedClaim))
                    await roles.RemoveClaimAsync(role, mark);
            }
        }
        await SynchronizeRolesAsync();
    }

    /// <summary>
    /// То, что делает запуск приложения (<c>Program.cs</c>): сверка ролей с объявленными.
    /// Перезапускать хост ради этого нечем — фикстура одна на весь прогон.
    /// </summary>
    private async Task SynchronizeRolesAsync()
    {
        using var scope = fixture.Services.CreateScope();
        await RoleSynchronizer.SyncAsync(
            scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>(),
            scope.ServiceProvider.GetRequiredService<BHS.CRG.Modules.PermissionCatalog>(),
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Тест"));
    }

    private async Task<IReadOnlyList<BHS.CRG.Domain.Activity.ActivityRecord>> RecordsAsync(ActivityAction action)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IActivityLog>().ReadAsync(0, 100, action.Code);
    }

    private Task<HttpClient> SignInAsync(string role) => SignInWithRoleAsync(role);

    /// <summary>Заводит пользователя с ролью и возвращает клиент с его токеном.</summary>
    private async Task<HttpClient> SignInWithRoleAsync(string role)
    {
        var email = $"role_{Guid.NewGuid():N}@test.local";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
