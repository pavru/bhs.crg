using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Tests.Integration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Support;

/// <summary>
/// Вход под ролью, состав которой перечислен в тесте: системной роли с нужным сочетанием прав может
/// не быть, а сочетание — как раз то, что проверяется.
///
/// <para>Вынесено сюда третьей копией (ревью PR #1251): то же самое — «роль с такими правами →
/// пользователь → токен» — уже стояло в <c>PermissionGateTests</c> и <c>McpToolGateTests</c>. Смена
/// формы входа или имени утверждения с правом потребовала бы трёх одинаковых правок, и одну из них
/// забыли бы.</para>
/// </summary>
internal static class GrantedSignIn
{
    private const string Password = "Passw0rd!Granted";

    /// <summary>Клиент с токеном пользователя, у которого ровно эти права — и никаких других.</summary>
    internal static async Task<HttpClient> WithPermissionsAsync(
        IntegrationTestFixture fixture, params string[] permissions)
    {
        var roleName = $"Granted_{Guid.NewGuid():N}";
        var email = $"granted_{Guid.NewGuid():N}@test.local";

        // Клиент — до области служб: первый вызов и поднимает хост.
        var client = fixture.CreateClient();
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

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
