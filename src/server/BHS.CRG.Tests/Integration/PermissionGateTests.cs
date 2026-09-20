using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
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
