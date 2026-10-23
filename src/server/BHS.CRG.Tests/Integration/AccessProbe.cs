using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Вход пользователем с одной системной ролью и чтение <c>GET /api/account/access</c> — общие для
/// классов, которые сверяют доступ ролей (ревью PR #1580). Жили копиями в каждом: правка формы входа
/// или ответа доступа требовала бы синхронной правки всех мест.
/// </summary>
internal static class AccessProbe
{
    private const string Password = "Passw0rd!";

    internal sealed record ModuleDto(string Code, string Title, bool Available, string[] Sections);

    internal sealed record AccessDto(string[] Permissions, ModuleDto[] Modules);

    /// <summary>
    /// Новый пользователь с ролью <paramref name="role" /> и клиент с его токеном. Префикс почты —
    /// чтобы по таблице пользователей было видно, какой класс его завёл.
    /// </summary>
    public static async Task<HttpClient> SignInAsync(IntegrationTestFixture fixture, string role, string prefix)
    {
        var email = $"{prefix}_{role.ToLowerInvariant()}_{Guid.NewGuid():N}@test.local";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Ответ <c>/api/account/access</c> для этого клиента.</summary>
    public static async Task<AccessDto> AccessAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/account/access");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccessDto>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }
}
