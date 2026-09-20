using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// <c>GET /api/account/access</c> — единственный источник, по которому клиент строит навигацию
/// (issue #952, ТЗ AUTH-14).
///
/// ⚠️ Проверяется здесь не «ответ непустой», а что ответ РАЗНЫЙ у разных ролей и совпадает с тем,
/// что на самом деле открывают ворота. Адрес, отвечающий одинаково всем, выглядит работающим и
/// делает навигацию по правам бессмысленной: меню будет одинаковым, а двери — нет.
/// </summary>
[Collection("Integration")]
public class AccountAccessTests(IntegrationTestFixture fixture)
{
    private const string Password = "Passw0rd!";

    [Fact]
    public async Task Access_lists_the_rights_of_this_user_and_not_of_another()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        var engineer = await SignInAsync(SystemRoles.IdEngineer);

        var adminAccess = await AccessAsync(admin);
        var engineerAccess = await AccessAsync(engineer);

        // Администратор получает всё объявленное, инженер — свой набор. Если бы адрес отвечал
        // одинаково, навигация по правам ничего бы не разграничивала.
        Assert.Contains(CorePermissions.UsersManage, adminAccess.Permissions);
        Assert.DoesNotContain(CorePermissions.UsersManage, engineerAccess.Permissions);

        // А общее у них есть — иначе тест проходил бы и на пустом ответе инженера.
        Assert.Contains(CorePermissions.CatalogRead, engineerAccess.Permissions);
        Assert.Contains(CorePermissions.CatalogRead, adminAccess.Permissions);
    }

    /// <summary>
    /// Ответ совпадает с воротами: право, названное в ответе, действительно открывает свой адрес, а
    /// неназванное — не открывает. Без этой сверки клиент мог бы рисовать меню по списку, который
    /// расходится с дверями, — и это худший случай: пункт виден и отвечает отказом.
    /// </summary>
    [Fact]
    public async Task What_access_promises_the_gates_deliver()
    {
        var engineer = await SignInAsync(SystemRoles.IdEngineer);
        var access = await AccessAsync(engineer);

        Assert.Contains(CorePermissions.CatalogRead, access.Permissions);
        Assert.Equal(HttpStatusCode.OK, (await engineer.GetAsync("/api/catalog")).StatusCode);

        Assert.DoesNotContain(CorePermissions.UsersManage, access.Permissions);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync("/api/users")).StatusCode);
    }

    /// <summary>
    /// Модуль помечен доступным ровно тогда, когда его адреса открываются. Правило одно на двоих —
    /// <see cref="BHS.CRG.Modules.ModuleAccess" />, — и тест следит, чтобы оно таким и осталось.
    /// </summary>
    [Fact]
    public async Task Module_flag_matches_the_module_gate()
    {
        var engineer = await SignInAsync(SystemRoles.IdEngineer);
        var access = await AccessAsync(engineer);

        var id = Assert.Single(access.Modules, m => m.Code == "id");
        Assert.True(id.Available);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await engineer.GetAsync("/api/quality-docs")).StatusCode);

        var accountant = await SignInAsync("Accountant");
        var closed = Assert.Single((await AccessAsync(accountant)).Modules, m => m.Code == "id");
        Assert.False(closed.Available);
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.GetAsync("/api/quality-docs")).StatusCode);
    }

    /// <summary>Без входа адрес не отвечает: он рассказывает о конкретном пользователе.</summary>
    [Fact]
    public async Task Anonymous_gets_nothing()
    {
        var response = await fixture.CreateClient().GetAsync("/api/account/access");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed record ModuleDto(string Code, string Title, bool Available);
    private sealed record AccessDto(string[] Permissions, ModuleDto[] Modules);

    private static async Task<AccessDto> AccessAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/account/access");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccessDto>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private async Task<HttpClient> SignInAsync(string role)
    {
        var email = $"acc_{role.ToLowerInvariant()}_{Guid.NewGuid():N}@test.local";
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
}
