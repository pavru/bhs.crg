using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Проверка прав на живом входе: политики <c>perm:</c> и <c>module:</c>, права не в токене,
/// смена ролей действует немедленно (issue #946, ТЗ AUTH-6, AUTH-7, AUTH-8).
///
/// ⚠️ Главный тест здесь — <see cref="Role_taken_away_refuses_the_next_request" />. Он написан
/// ПЕРВЫМ и на прежнем коде не проходил: токен жил до истечения срока со своей ролью, и снятие
/// роли не значило ничего. Это и есть доказательство, что тест проверяет требование, а не сам себя.
/// </summary>
[Collection("Integration")]
public class PermissionGateTests(IntegrationTestFixture fixture)
{
    private const string Password = "Passw0rd!";

    /// <summary>Заводит пользователя с ролью и возвращает клиент с его токеном.</summary>
    private async Task<(HttpClient Client, Guid Id, string Email)> SignInAsync(string role)
    {
        var email = $"{role.ToLowerInvariant()}_{Guid.NewGuid():N}@test.local";
        Guid id;
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            id = user.Id;
        }

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(client, email));
        return (client, id, email);
    }

    private static async Task<string> TokenAsync(HttpClient client, string email)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    /// <summary>
    /// Снять роль у вошедшего — и его СЛЕДУЮЩИЙ запрос обязан отказать, с тем же токеном на руках.
    ///
    /// Так выглядит отзыв доступа в жизни: администратор снимает роль, а человек в это время сидит
    /// в приложении. Пока токен переживал снятие роли, отзыв доступа означал «через час».
    /// </summary>
    [Fact]
    public async Task Role_taken_away_refuses_the_next_request()
    {
        var (admin, _, _) = await SignInAsync(SystemRoles.Admin);          // тот, кто снимает
        var (victim, victimId, _) = await SignInAsync(SystemRoles.Admin);  // тот, у кого снимают

        // До снятия роли доступ есть — иначе проверка ниже ничего не значила бы.
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync("/api/users")).StatusCode);

        var changed = await admin.PutAsJsonAsync($"/api/users/{victimId}/role", new { role = SystemRoles.IdEngineer });
        changed.EnsureSuccessStatusCode();

        // Тот же токен, следующий запрос.
        var after = await victim.GetAsync("/api/users");
        Assert.True(after.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Снятая роль не подействовала: ответ {(int)after.StatusCode}, а доступ обязан быть закрыт.");
    }

    /// <summary>
    /// Отказ по снятой роли не выкидывает человека на страницу входа: refresh-сессия цела, обмен
    /// даёт новый токен, и работа продолжается — уже с новыми правами (AUTH-7, «перелогин не
    /// требуется»). Если бы снятие роли рвало и refresh, отзыв одного права выглядел бы как
    /// «меня разлогинило».
    /// </summary>
    [Fact]
    public async Task Refusal_does_not_cost_a_relogin()
    {
        var (admin, _, _) = await SignInAsync(SystemRoles.Admin);

        var email = $"kept_{Guid.NewGuid():N}@test.local";
        Guid id;
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, SystemRoles.Admin)).Succeeded);
            id = user.Id;
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        var pair = await login.Content.ReadFromJsonAsync<JsonElement>();
        var refresh = pair.GetProperty("refreshToken").GetString();

        (await admin.PutAsJsonAsync($"/api/users/{id}/role", new { role = SystemRoles.IdEngineer }))
            .EnsureSuccessStatusCode();

        var renewed = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = refresh });
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);

        // Новый токен выдан без пароля — но прав администратора в нём уже нет.
        var token = (await renewed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
    }

    /// <summary>
    /// Обслуживание экземпляра закрыто правом, а не ролью (issue #947, ТЗ CORE-37.1). Инженер ИД
    /// вошёл и работает — но копии, обновление, почта и внешние службы ему не отвечают.
    ///
    /// Раньше на этих группах стояло имя роли. Разница не косметическая: пока ворота стоят на
    /// имени, состав доступа нельзя ни увидеть в редакторе ролей, ни изменить, не трогая код.
    /// </summary>
    /// <remarks>
    /// Адреса взяты из кода, а не придуманы: первая редакция теста спрашивала выдуманные пути и
    /// получала 404 — проверка прав до такого ответа не доходит вовсе. Отказ по несуществующему
    /// адресу выглядит как отказ по праву ровно настолько, чтобы обмануть невнимательный тест.
    /// </remarks>
    /// <remarks>
    /// ⚠️ Один вход на все адреса, и это не экономия строк. Вход ограничен по частоте, а
    /// ограничитель общий на прогон: редакция с [Theory] входила заново на каждый адрес и выбивала
    /// 429 у ЧУЖИХ тестов, падавших следом. Проверка, которая роняет соседей, не проверка.
    /// </remarks>
    [Fact]
    public async Task Instance_upkeep_refuses_a_user_without_the_system_right()
    {
        string[] addresses =
        [
            "/api/backup/size", "/api/backup/files",
            "/api/settings/integrations", "/api/settings/integrations/models",
        ];

        var (engineer, _, _) = await SignInAsync(SystemRoles.IdEngineer);

        foreach (var address in addresses)
            Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync(address)).StatusCode);

        // Метод тоже берётся из кода: GET по адресу, который умеет только POST, отвечает 405 — и
        // это опять ответ ДО проверки прав. Третий раз за задачу отказ не по той причине выглядел
        // как отказ по праву.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await engineer.PostAsync("/api/system/update/check", null)).StatusCode);

        // ⚠️ А вот СТАТУС версии обязан остаться открытым любому вошедшему (issue #813). Сначала
        // под право ушла вся группа /api/system — «обновления это же обслуживание», — и 403 стал
        // приходить на КАЖДОМ экране: статус читает подвал боковой панели. Найдено ревью, поэтому
        // проверка стоит здесь же, рядом с воротами, которые её чуть не съели.
        Assert.Equal(HttpStatusCode.OK, (await engineer.GetAsync("/api/system/update")).StatusCode);
    }

    /// <summary>
    /// Те же адреса открыты «Администратору» — иначе предыдущий тест доказывал бы лишь то, что
    /// адреса сломаны для всех.
    /// </summary>
    [Fact]
    public async Task Instance_upkeep_opens_for_the_administrator()
    {
        var (admin, _, _) = await SignInAsync(SystemRoles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/backup/files")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/settings/integrations")).StatusCode);
    }

    /// <summary>
    /// Файлы хранилища — под правом (ТЗ CORE-37.2).
    ///
    /// ⚠️ Отказ проверяется ролью БЕЗ права, заведённой здесь же. Сначала проверка опиралась на
    /// «Руководителя» — единственную системную роль без <c>core.files.use</c>, — но ревью показало,
    /// чего стоило это исключение: диалог «сообщить об ошибке» грузит снимок ДО отправки, и отказ
    /// съедал не вложение, а всё сообщение. Право роли выдано, и опереться на неё больше нельзя:
    /// тест, привязанный к составу роли, ломается от каждой правки этого состава.
    ///
    /// ⚠️ Тест доказывает ровно то, что написано: без права дверь не открывается. Он НЕ доказывает,
    /// что чужой файл недостижим, — выдача идёт по пути и владельца не сверяет.
    /// </summary>
    [Fact]
    public async Task Files_need_the_files_right()
    {
        var roleName = $"NoFiles_{Guid.NewGuid():N}";
        var email = $"nofiles_{Guid.NewGuid():N}@test.local";

        using (var scope = fixture.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(roleName))).Succeeded);
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
        }

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await TokenAsync(client, email));

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/attachments?path=any/known/path.pdf")).StatusCode);

        // У «Инженера ИД» право есть — отказ приходит не от ворот, а от отсутствия файла.
        var (engineer, _, _) = await SignInAsync(SystemRoles.IdEngineer);
        Assert.Equal(HttpStatusCode.NotFound,
            (await engineer.GetAsync("/api/attachments?path=any/known/path.pdf")).StatusCode);
    }

    /// <summary>Право есть — дверь открыта.</summary>
    [Fact]
    public async Task Permission_opens_the_door()
    {
        var (admin, _, _) = await SignInAsync(SystemRoles.Admin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/users")).StatusCode);
    }

    /// <summary>
    /// Права нет — отказ 403, а не 401: вошёл, но не допущен. Разница видна клиенту, который на
    /// 401 идёт обновлять токен, а на 403 обязан показать честную страницу (AUTH-15).
    /// </summary>
    [Fact]
    public async Task Missing_permission_refuses_with_forbidden()
    {
        var (engineer, _, _) = await SignInAsync(SystemRoles.IdEngineer);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync("/api/users")).StatusCode);
    }

    /// <summary>Ворота модуля: у инженера ИД права модуля есть, и адреса модуля ему открыты.</summary>
    [Fact]
    public async Task Module_gate_admits_a_user_with_module_permissions()
    {
        var (engineer, _, _) = await SignInAsync(SystemRoles.IdEngineer);
        Assert.Equal(HttpStatusCode.OK, (await engineer.GetAsync("/api/quality-docs")).StatusCode);
    }

    /// <summary>
    /// У бухгалтера прав модуля исполнительной документации нет — и адреса модуля ему закрыты
    /// целиком, воротами на группе (AUTH-10), а не проверкой в каждом обработчике.
    /// </summary>
    [Fact]
    public async Task Module_gate_refuses_a_user_without_module_permissions()
    {
        var (accountant, _, _) = await SignInAsync("Accountant");
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync("/api/quality-docs")).StatusCode);
    }

    /// <summary>
    /// Дверь открывает ПРАВО, а не имя роли: пользователь без «Администратора», но с ролью, в
    /// составе которой есть <c>core.users.manage</c>, проходит.
    ///
    /// Этот тест отличает сделанное от прежнего. Проверка «инженеру ИД отказано» прошла бы и на
    /// старых воротах <c>RequireAuthorization("Admin")</c> — она ничего не доказывает про права.
    /// Здесь же роль заведомо не «Администратор», и пройти можно только по составу прав.
    /// </summary>
    [Fact]
    public async Task Permission_from_any_role_opens_the_door()
    {
        var roleName = $"Granted_{Guid.NewGuid():N}";
        var email = $"granted_{Guid.NewGuid():N}@test.local";

        using (var scope = fixture.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var role = new IdentityRole<Guid>(roleName);
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            Assert.True((await roles.AddClaimAsync(
                role, new Claim(RoleSynchronizer.PermissionClaim, "core.users.manage"))).Succeeded);

            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
        }

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(client, email));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);
    }

    /// <summary>
    /// Отправка по почте закрыта СВОИМ правом (ТЗ ID-4.1, issue #989).
    ///
    /// ⚠️ Проверяется двусторонне, и обе стороны нужны. Отказ без права прошёл бы и на прежних
    /// воротах <c>RequireAuthorization("Admin")</c> — он ничего не доказывает про право. А проход с
    /// правом прошёл бы и в том случае, если бы ворота сняли вовсе.
    ///
    /// Различаем 403 и 404: комплекта с таким идентификатором нет, поэтому пропущенный запрос
    /// обязан дойти до обработчика и не найти комплект. Совпади коды — тест не отличал бы
    /// «не пустили» от «пустили».
    /// </summary>
    [Fact]
    public async Task Sending_by_email_is_closed_by_its_own_permission()
    {
        var missing = Guid.NewGuid();
        var letter = new { to = new[] { "someone@example.com" }, subject = "Тема", body = "Текст" };

        var reader = await SignInWithPermissionsAsync("id.document.read");
        var sender = await SignInWithPermissionsAsync("id.document.read", "id.document.send");

        foreach (var url in new[]
        {
            $"/api/document-sets/{missing}/email",
            $"/api/document-sets/{missing}/documents/{Guid.NewGuid()}/email",
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(url, letter)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await sender.PostAsJsonAsync(url, letter)).StatusCode);
        }
    }

    /// <summary>Клиент с ролью, состав которой перечислен здесь и нигде не объявлен.</summary>
    private async Task<HttpClient> SignInWithPermissionsAsync(params string[] permissions)
    {
        var roleName = $"Granted_{Guid.NewGuid():N}";
        var email = $"granted_{Guid.NewGuid():N}@test.local";

        using (var scope = fixture.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var role = new IdentityRole<Guid>(roleName);
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            foreach (var code in permissions)
                Assert.True((await roles.AddClaimAsync(
                    role, new Claim(RoleSynchronizer.PermissionClaim, code))).Succeeded);

            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
        }

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await TokenAsync(client, email));
        return client;
    }

    /// <summary>
    /// Каждые ворота на праве называют ОБЪЯВЛЕННОЕ право — проверяется по всем адресам живого
    /// приложения.
    ///
    /// Дверь на необъявленное право не открыть никому: право нельзя выдать ни одной роли, и даже
    /// «Администратор», получающий всё объявленное, не получит несуществующего. Отвечает такая
    /// дверь обычным «нельзя», то есть опечатка выглядит как правильная работа прав и разбирается
    /// как «почему у меня нет доступа». Сборка политики такое имя отвергает — этот тест
    /// заставляет её собраться на каждом адресе, то есть ловит опечатку в CI, а не на экземпляре.
    ///
    /// Это же — зародыш инвентаризации адресов (AUTH-9): она придёт вместе с воротами на остальные
    /// адреса и будет требовать ворота у каждого, а не только сверять названное.
    /// </summary>
    [Fact]
    public async Task Every_permission_gate_names_a_declared_permission()
    {
        _ = fixture.CreateClient();
        var policies = fixture.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var named = fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .SelectMany(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>())
            .Select(a => a.Policy)
            .Where(p => p is not null && p.StartsWith(AppPolicies.PermissionPrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(named);   // ворот на правах не осталось — значит тест проверяет пустоту

        foreach (var policy in named)
            Assert.NotNull(await policies.GetPolicyAsync(policy!));
    }

    /// <summary>
    /// Прав в токене нет (AUTH-6). Иначе отзыв права дожидался бы истечения срока — ровно там, где
    /// ждать опаснее всего.
    /// </summary>
    [Fact]
    public async Task Token_carries_no_permissions()
    {
        var client = fixture.CreateClient();
        var email = $"claims_{Guid.NewGuid():N}@test.local";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, SystemRoles.Admin)).Succeeded);
        }

        var token = new JwtSecurityTokenHandler().ReadJwtToken(await TokenAsync(client, email));

        Assert.DoesNotContain(token.Claims, c => c.Type == RoleSynchronizer.PermissionClaim);
        Assert.DoesNotContain(token.Claims, c => c.Value.StartsWith("core.", StringComparison.Ordinal));
    }
}
