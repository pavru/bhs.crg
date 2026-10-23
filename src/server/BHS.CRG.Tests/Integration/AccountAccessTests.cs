using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Settings;
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

    /// <summary>
    /// Каждый модуль называет разделы ядра, которыми пользуется (issue #1001): из них клиент собирает
    /// навигацию рабочего пространства. Ответ отдаёт объявление модуля как есть; что объявления
    /// годны и у счетов не пусты — <c>CoreSectionsTests</c> (модуль счетов здесь не включён).
    /// </summary>
    [Fact]
    public async Task Модули_называют_разделы_ядра_для_пространств()
    {
        var access = await AccessAsync(await SignInAsync("Admin"));

        var id = Assert.Single(access.Modules, m => m.Code == "id");
        Assert.Equal(new BHS.CRG.Api.Modules.IdModule().UsesSections, id.Sections);
        Assert.Contains("constructions", id.Sections);
    }

    /// <summary>
    /// Классификатор видов работ по системным ролям (WORK-59): ведут его администратор, сметчик и инженер
    /// ИД — последний правил виды работ общим адресом под <c>core.catalog.edit</c>, и с дверью видов работ
    /// без своего права потерял бы эту правку молча. Остальным ролям право не выдаётся.
    /// </summary>
    [Fact]
    public async Task Классификатор_видов_работ_ведут_ровно_администратор_сметчик_и_инженер_ИД()
    {
        string[] keepers = [SystemRoles.Admin, SystemRoles.IdEngineer, "Estimator"];

        foreach (var role in SystemRoles.All)
        {
            var access = await AccessAsync(await SignInAsync(role.Name));
            Assert.True(keepers.Contains(role.Name) == access.Permissions.Contains(CorePermissions.WorkTypesEdit),
                $"Роль «{role.Name}»: core.worktypes.edit {(keepers.Contains(role.Name) ? "не выдано" : "выдано лишним")}");
        }
    }

    /// <summary>Без входа адрес не отвечает: он рассказывает о конкретном пользователе.</summary>
    [Fact]
    public async Task Anonymous_gets_nothing()
    {
        var response = await fixture.CreateClient().GetAsync("/api/account/access");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// «Мои права» в профиле (issue #954, ТЗ AUTH-16.6): человек видит СВОИ права словами, а не
    /// кодами, и видит ровно свои.
    ///
    /// Зачем это в профиле: вопрос «почему у меня нет раздела» задаётся уже после отказа, и без
    /// ответа его несут администратору. Список, показывающий чужие права, отвечал бы на него
    /// неверно — то есть хуже, чем не отвечал бы вовсе.
    /// </summary>
    [Fact]
    public async Task Мои_права_показываются_словами_и_только_свои()
    {
        var engineer = await SignInAsync(SystemRoles.IdEngineer);
        var groups = await MyPermissionsAsync(engineer);

        var codes = groups.SelectMany(g => g.Permissions).Select(p => p.Code).ToList();
        Assert.Contains(CorePermissions.CatalogRead, codes);
        Assert.DoesNotContain(CorePermissions.UsersManage, codes);

        // Каждое право объяснено — иначе профиль показывал бы те же коды, только в рамке.
        Assert.All(groups.SelectMany(g => g.Permissions), p => Assert.False(string.IsNullOrWhiteSpace(p.Gives)));
        // И у каждой группы есть название для человека: «Ядро», а не «core».
        Assert.All(groups, g => Assert.False(string.IsNullOrWhiteSpace(g.Title)));
        Assert.Contains(groups, g => g.Title == "Ядро");
    }

    /// <summary>
    /// У администратора список шире, и пустых групп в нём нет. Группа без прав — это заголовок,
    /// под которым ничего не написано: человек читает его как «здесь у меня что-то есть».
    /// </summary>
    [Fact]
    public async Task У_администратора_прав_больше_и_пустых_групп_нет()
    {
        var admin = await SignInAsync(SystemRoles.Admin);
        var engineer = await SignInAsync(SystemRoles.IdEngineer);

        var adminGroups = await MyPermissionsAsync(admin);
        var engineerGroups = await MyPermissionsAsync(engineer);

        Assert.True(adminGroups.SelectMany(g => g.Permissions).Count()
                  > engineerGroups.SelectMany(g => g.Permissions).Count());
        Assert.All(adminGroups, g => Assert.NotEmpty(g.Permissions));
    }

    /// <summary>Свои права — личное: без входа спрашивать их не у кого.</summary>
    [Fact]
    public async Task Без_входа_своих_прав_нет()
    {
        var response = await fixture.CreateClient().GetAsync("/api/account/permissions");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Пояс компании — каждому вошедшему, не только администратору (issue #1463): «сегодня» в календаре
    /// считает по нему любой экран. Имя — IANA: клиент отдаёт его прямо в <c>Intl</c>. Сохранённый пояс
    /// задан Windows-именем — переводится; неизвестный машине — действует пояс сервера, как у часов
    /// модулей, а не «пусто».
    /// </summary>
    [Fact]
    public async Task Пояс_компании_доходит_до_каждого_именем_IANA()
    {
        var engineer = await SignInAsync(SystemRoles.IdEngineer);
        // Настройка общая для всей коллекции: возвращаем ту, что была, а не «пусто» (ревью PR #1548).
        var before = await StoredZoneAsync();
        try
        {
            await StoreZoneAsync("Asia/Vladivostok");
            Assert.Equal("Asia/Vladivostok", await ZoneAsync(engineer));

            await StoreZoneAsync("Russian Standard Time");
            Assert.Equal("Europe/Moscow", await ZoneAsync(engineer));

            await StoreZoneAsync("Mars/Olympus");
            Assert.Equal(CompanyZone.IanaId(TimeZoneInfo.Local), await ZoneAsync(engineer));
        }
        finally { await StoreZoneAsync(before); }
    }

    private async Task<string?> StoredZoneAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>().GetAsync(AppSettingKeys.CompanyTimeZone);
    }

    private async Task StoreZoneAsync(string? zone)
    {
        using var scope = fixture.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>().SetAsync(AppSettingKeys.CompanyTimeZone, zone);
    }

    private static async Task<string?> ZoneAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/account/access"))
            .GetProperty("company").GetProperty("timeZone").GetString();

    private sealed record PermissionDto(string Code, string Gives, string Opens, string[] UsuallyWith);
    private sealed record PermissionGroupDto(string Module, string Title, PermissionDto[] Permissions);

    private static async Task<PermissionGroupDto[]> MyPermissionsAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/account/permissions");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PermissionGroupDto[]>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
    }

    private static Task<AccessProbe.AccessDto> AccessAsync(HttpClient client) => AccessProbe.AccessAsync(client);

    private Task<HttpClient> SignInAsync(string role) => AccessProbe.SignInAsync(fixture, role, "acc");
}
