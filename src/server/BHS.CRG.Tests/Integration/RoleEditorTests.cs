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
using System.Security.Claims;

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
    /// прибирать мы, и с обеих сторон.
    ///
    /// Прибирается двое. Правка состава СИСТЕМНОЙ роли переживает перезапуск (в том и смысл
    /// задачи), то есть оставленная — переживёт и весь остальной прогон: роль «Инженер ИД» с одним
    /// правом ломала бы чужие тесты там, где про роли не сказано ни слова. А заведённые тестами
    /// роли копятся на постоянной базе разработчика, и список ролей спрашивает носителей у каждой.
    /// </summary>
    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        await CleanRolesAsync();
    }

    public Task DisposeAsync() => CleanRolesAsync();

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
    /// Заведённую роль можно НАЗНАЧИТЬ, и она действует.
    ///
    /// ⚠️ Назначение идёт через живой адрес <c>/api/users</c>, а не через <c>UserManager</c>, как в
    /// остальных проверках этого класса. Разница решающая: назначение — единственный путь, которым
    /// роль попадает к человеку, и пока оно сверяло имя со списком СИСТЕМНЫХ ролей, заведённая
    /// роль создавалась, права ей выдавались, а носить её было некому. Проверка, идущая мимо
    /// адреса, этого не видит вовсе (поймано ревью PR #981).
    /// </summary>
    [Fact]
    public async Task Заведённую_роль_можно_назначить_пользователю_и_она_действует()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var title = Title("Разбор обращений");
        var role = await CreateRoleAsync(admin, title, [CorePermissions.SupportReview]);

        var email = $"role_{Guid.NewGuid():N}@test.local";
        var created = await admin.PostAsJsonAsync("/api/users", new
        {
            email, displayName = "Тест", password = Password, roles = new[] { role },
        });
        created.EnsureSuccessStatusCode();

        // Ответ называет и техническое имя, и подпись: имя вида role-1a2b3c4d человеку не показать.
        var dto = await created.Content.ReadFromJsonAsync<JsonElement>();
        var assigned = dto.GetProperty("roles").EnumerateArray().Single();
        Assert.Equal(role, assigned.GetProperty("name").GetString());
        Assert.Equal(title, assigned.GetProperty("title").GetString());

        // Роль действует: выданная дверь открыта, соседняя закрыта.
        var clerk = await SignInAsync(email);
        Assert.Equal(HttpStatusCode.OK, (await clerk.GetAsync("/api/bug-reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync("/api/users")).StatusCode);

        // И в журнале — название роли, а не её техническое имя.
        var record = Assert.Single(await RecordsAsync(ActivityActions.UserCreated));
        Assert.Equal(title, record.After);
    }

    /// <summary>Роли, которой нет, назначить нельзя — и отказ называет, что именно не нашлось.</summary>
    [Fact]
    public async Task Несуществующую_роль_назначить_нельзя()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var refused = await admin.PostAsJsonAsync("/api/users", new
        {
            email = $"role_{Guid.NewGuid():N}@test.local",
            displayName = "Тест", password = Password, roles = new[] { "role-нетакой" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("role-нетакой", await refused.Content.ReadAsStringAsync());
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
    /// Роль «все права» составом не правится (ТЗ AUTH-5 — «Администратора» нельзя лишить
    /// <c>core.users.manage</c>; причина здесь шире).
    ///
    /// Состав у неё не перечислен: он равен справочнику и пополняется вместе с ним. Разреши мы
    /// правку — состав замер бы на сегодняшнем справочнике, и право следующего выпуска не
    /// досталось бы никому.
    /// </summary>
    [Fact]
    public async Task Роль_все_права_составом_не_правится()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var refused = await admin.PutAsJsonAsync($"/api/roles/{SystemRoles.Admin}/permissions",
            new { permissions = new[] { CorePermissions.CatalogRead } });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains(CorePermissions.UsersManage, await refused.Content.ReadAsStringAsync());

        // И право осталось на месте: отказ обязан быть отказом, а не половиной правки.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/users")).StatusCode);
    }

    /// <summary>
    /// Роль «все права» получает НОВОЕ право при старте, даже если кто-то отметил её правленой.
    ///
    /// ⚠️ Проверка сделана нарушением: отметка правки и снятое право ставятся прямо в базе — через
    /// редактор такого не сделать. Без этого правила замороженный состав «Администратора» означал
    /// бы, что право очередного выпуска и права нового модуля не достаются никому: 403 у всех
    /// сразу, без строки в логе и без связи с той давней правкой (поймано ревью PR #981).
    /// </summary>
    [Fact]
    public async Task Роль_все_права_получает_новое_право_при_старте()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        using (var scope = fixture.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var role = (await roles.FindByNameAsync(SystemRoles.Admin))!;
            await roles.AddClaimAsync(role, new Claim(RoleSynchronizer.EditedClaim, "да"));
            foreach (var code in new[] { CorePermissions.ViewsShare, CorePermissions.UsersManage })
                await roles.RemoveClaimAsync(role, new Claim(RoleSynchronizer.PermissionClaim, code));
        }

        await SynchronizeRolesAsync();

        var after = await PermissionsOfAsync(admin, SystemRoles.Admin);
        Assert.Contains(CorePermissions.ViewsShare, after);     // новое право дошло
        Assert.Contains(CorePermissions.UsersManage, after);    // и управление вернулось
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

    /// <summary>
    /// Код права принимается в любом регистре и записывается в каноническом.
    ///
    /// Справочник отвечает про право без учёта регистра, а дальше код живёт строкой: записанный как
    /// <c>CORE.CATALOG.READ</c>, он разошёлся бы и с галкой в редакторе, и с проверками — право
    /// выглядело бы выданным и не совпадало бы ни с чем (поймано ревью PR #981).
    /// </summary>
    [Fact]
    public async Task Код_права_записывается_в_каноническом_виде()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var role = await CreateRoleAsync(admin, Title("Регистр"),
            [CorePermissions.CatalogRead.ToUpperInvariant()]);

        Assert.Equal([CorePermissions.CatalogRead], await PermissionsOfAsync(admin, role));
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

    /// <summary>
    /// Список ролей упорядочен ПО НАЗВАНИЮ — тому, которое читает человек.
    ///
    /// ⚠️ Проверка не косметическая. Этот же список — выпадающий на экране «Пользователи», и пока
    /// он шёл по техническому имени, первой оказывалась <c>Accountant</c>: диалог создания брал из
    /// него умолчание, и новый сотрудник заводился «Бухгалтером» — с правом отмечать оплату и
    /// закрывать период (ревью #983). Умолчание убрано, но порядок обязан быть читаемым.
    /// </summary>
    [Fact]
    public async Task Роли_упорядочены_по_названию()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/roles");
        var titles = list.EnumerateArray().Select(r => r.GetProperty("title").GetString()!).ToList();

        Assert.Equal([.. titles.OrderBy(t => t, StringComparer.CurrentCulture)], titles);

        // Пара, на которой два порядка расходятся: по названию «Администратор» раньше «Бухгалтера»,
        // по техническому имени — наоборот (Accountant раньше Admin). Без неё проверка выше прошла
        // бы и на сортировке по имени, если названия случайно легли тем же порядком.
        Assert.True(titles.IndexOf("Администратор") < titles.IndexOf("Бухгалтер"),
            "порядок совпал с сортировкой по техническому имени");
    }

    /// <summary>
    /// Роль «все права» помечена признаком, и редактор узнаёт о запрете ДО щелчка.
    ///
    /// ⚠️ Отказ сервера (409) был, а признака не было: экран рисовал у «Администратора» обычные
    /// галки, щёлкал ими и получал отказ на сохранение — запрет, о котором узнаёшь, только нарушив
    /// его (ревью #983).
    /// </summary>
    [Fact]
    public async Task Роль_все_права_помечена_признаком()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        var custom = await CreateRoleAsync(admin, Title("Обычная"), [CorePermissions.CatalogRead]);

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/roles");
        var byName = list.EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!);

        Assert.True(byName[SystemRoles.Admin].GetProperty("allPermissions").GetBoolean());
        Assert.False(byName[custom].GetProperty("allPermissions").GetBoolean());
        Assert.False(byName[SystemRoles.IdEngineer].GetProperty("allPermissions").GetBoolean());
    }

    /// <summary>
    /// Подпись роли в профиле и в списке пользователей — та же, что в редакторе.
    ///
    /// Проверка держит дешёвый путь получения названия (<c>TitleAsync</c>/<c>TitlesAsync</c>),
    /// заведённый вместо полного вида роли: тот ради одной подписи поднимал всех её носителей
    /// (ревью #983). Ошибись этот путь — подписи разойдутся между экранами.
    /// </summary>
    [Fact]
    public async Task Подпись_роли_одинакова_в_профиле_и_в_списке_пользователей()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        var title = Title("Кладовщик");
        var role = await CreateRoleAsync(admin, title, [CorePermissions.CatalogRead]);
        var holder = await SignInWithRoleAsync(role);

        var profile = await holder.GetFromJsonAsync<JsonElement>("/api/account");
        Assert.Equal(title, profile.GetProperty("roles").EnumerateArray().Single().GetProperty("title").GetString());

        var users = await admin.GetFromJsonAsync<JsonElement>("/api/users");
        var row = users.EnumerateArray().First(
            u => u.GetProperty("roles").EnumerateArray().Any(r => r.GetProperty("name").GetString() == role));
        Assert.Equal(title, row.GetProperty("roles").EnumerateArray().Single().GetProperty("title").GetString());
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
    /// Возвращает системные роли к объявленному составу и уносит заведённые тестами.
    ///
    /// Отметку «правил администратор» снимаем первой: именно она делает правку живучей — без её
    /// снятия уборка ничего не убрала бы.
    /// </summary>
    private async Task CleanRolesAsync()
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

            // Заведённые редактором роли узнаются по имени: его даёт он сам (role-xxxxxxxx).
            foreach (var mine in roles.Roles.Where(r => r.Name!.StartsWith("role-")).ToList())
                await roles.DeleteAsync(mine);
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

    /// <summary>Вход уже заведённой учётной записью — по почте.</summary>
    private async Task<HttpClient> SignInAsync(string emailOrRole)
    {
        if (!emailOrRole.Contains('@')) return await SignInWithRoleAsync(emailOrRole);

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = emailOrRole, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

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
