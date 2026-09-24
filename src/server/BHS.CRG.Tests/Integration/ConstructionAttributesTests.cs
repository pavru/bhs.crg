using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Settings;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Часовой пояс и внешний идентификатор стройки, настройка пояса компании (ТЗ CORE-5, issue #960).
///
/// <para>Проверяются ПУТИ ЗАПИСИ целиком — от адреса до базы и обратно. Инвариант «внешний
/// идентификатор только парой» и проверка пояса живут в разных слоях (домен и адрес), и по
/// отдельности каждый выглядит покрытым: пока запрос не прошёл насквозь, неизвестно, доходит ли
/// отказ до вызывающего понятным текстом или превращается в «внутреннюю ошибку сервера».</para>
/// </summary>
[Collection("Integration")]
public class ConstructionAttributesTests(IntegrationTestFixture fixture)
{
    private const string Password = "Test-1234!";

    [Fact]
    public async Task Time_zone_is_set_cleared_and_refused_when_unknown()
    {
        await fixture.ResetDatabaseAsync();
        var client = await SignInAsync();
        var id = await CreateConstructionAsync(client, "Стройка с поясом");

        // Своего пояса нет — это «как у компании», а не пустая строка и не пояс компании в поле.
        Assert.Null(await ReadTimeZoneAsync(client, id));

        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"/api/constructions/{id}/timezone",
                new { timeZoneId = "Asia/Yekaterinburg" })).StatusCode);
        Assert.Equal("Asia/Yekaterinburg", await ReadTimeZoneAsync(client, id));

        // Пустое значение СНИМАЕТ пояс: стройка возвращается к поясу компании, а не остаётся с
        // последним выбранным.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"/api/constructions/{id}/timezone",
                new { timeZoneId = (string?)null })).StatusCode);
        Assert.Null(await ReadTimeZoneAsync(client, id));

        // Несуществующий пояс — отказ ЗДЕСЬ. Прими его система, и отказ пришёл бы при первом
        // подсчёте суток, далеко от места, где пояс ввели.
        var refused = await client.PutAsJsonAsync($"/api/constructions/{id}/timezone",
            new { timeZoneId = "Mars/Olympus" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task External_id_takes_a_pair_refuses_half_a_pair_and_refuses_a_taken_code()
    {
        await fixture.ResetDatabaseAsync();
        var client = await SignInAsync();
        var first = await CreateConstructionAsync(client, "Первая");
        var second = await CreateConstructionAsync(client, "Вторая");

        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"/api/constructions/{first}/external-id",
                new { system = "1С", code = "СТР-0001" })).StatusCode);

        var read = await ReadConstructionAsync(client, first);
        Assert.Equal("1С", read.GetProperty("externalSystem").GetString());
        Assert.Equal("СТР-0001", read.GetProperty("externalCode").GetString());

        // Половина пары не адресует ничего: коды разных систем пересекаются свободно.
        var half = await client.PutAsJsonAsync($"/api/constructions/{first}/external-id",
            new { system = "1С", code = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, half.StatusCode);

        // Занятый код — 409, а НЕ 500. До проверки в обработчике повтор доезжал до уникального
        // индекса, и 23505 уходил наружу «внутренней ошибкой сервера» (ревью PR #1046).
        var taken = await client.PutAsJsonAsync($"/api/constructions/{second}/external-id",
            new { system = "1С", code = "СТР-0001" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);

        // ⚠️ И текст обязан НАЗВАТЬ занявшую стройку. Без этой проверки тест проходит даже со
        // снятой проверкой в обработчике: 409 придёт от перехвата гонки на адресе, у которого имени
        // нет. Поймано подсадкой — тест зеленел на сломанном коде и подтверждал не то.
        var message = await taken.Content.ReadAsStringAsync();
        Assert.Contains("Первая", message);

        // Слишком длинный код — тоже отказ вызывающему, а не отказ базы по ширине колонки (22001).
        var tooLong = await client.PutAsJsonAsync($"/api/constructions/{second}/external-id",
            new { system = "1С", code = new string('Ц', 300) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task Company_time_zone_defaults_to_the_server_and_refuses_an_unknown_zone()
    {
        await fixture.ResetDatabaseAsync();
        var client = await SignInAsync();

        var before = await ReadJsonAsync(client, "/api/settings/company");
        // Настройки нет — действует пояс сервера. Именно так, а не отказом старта: обновление у
        // заказчика, которому учёт работ ещё не нужен, не должно останавливаться ради пояса.
        Assert.Equal(JsonValueKind.Null, before.GetProperty("timeZoneId").ValueKind);
        Assert.Equal(TimeZoneInfo.Local.Id, before.GetProperty("effectiveTimeZoneId").GetString());

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync("/api/settings/company", new { timeZoneId = "Europe/Moscow" })).StatusCode);

        var after = await ReadJsonAsync(client, "/api/settings/company");
        Assert.Equal("Europe/Moscow", after.GetProperty("timeZoneId").GetString());
        Assert.Equal("Europe/Moscow", after.GetProperty("effectiveTimeZoneId").GetString());
        Assert.True(after.GetProperty("resolved").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PutAsJsonAsync("/api/settings/company", new { timeZoneId = "Mars/Olympus" })).StatusCode);

        using var scope = fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IAppSettingsStore>();
        Assert.Equal("Europe/Moscow", (await store.GetCompanyTimeZoneAsync()).Id);

        // Снятие настройки возвращает пояс сервера — «вернуть умолчание», а не «пояс неизвестен».
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PutAsJsonAsync("/api/settings/company", new { timeZoneId = (string?)null })).StatusCode);
        Assert.Equal(TimeZoneInfo.Local.Id, (await store.GetCompanyTimeZoneAsync()).Id);
    }

    /// <summary>
    /// Круговой рейс через резервную копию: новые колонки и настройка экземпляра обязаны пережить
    /// export → wipe → import.
    ///
    /// <para>Сторож покрытия манифеста сверяет СУЩНОСТИ, а не их свойства: забытая колонка прошла бы
    /// там зелёной, и потеря обнаружилась бы только при восстановлении у заказчика — то есть тогда,
    /// когда исходной системы уже нет (ревью PR #1046).</para>
    /// </summary>
    [Fact]
    public async Task Backup_round_trip_keeps_the_time_zone_external_id_and_company_setting()
    {
        await fixture.ResetDatabaseAsync();
        var client = await SignInAsync();
        var id = await CreateConstructionAsync(client, "Стройка для копии");
        await client.PutAsJsonAsync($"/api/constructions/{id}/timezone", new { timeZoneId = "Asia/Yekaterinburg" });
        await client.PutAsJsonAsync($"/api/constructions/{id}/external-id", new { system = "1С", code = "СТР-0042" });
        await client.PutAsJsonAsync("/api/settings/company", new { timeZoneId = "Europe/Moscow" });

        // Копия снимается службой, а не адресом: снятие через каталог файлов ничего не добавило бы
        // проверке, а круговой рейс здесь — про содержимое манифеста.
        using var exportScope = fixture.Services.CreateScope();
        var service = new BackupService(
            exportScope.ServiceProvider.GetRequiredService<AppDbContext>(),
            exportScope.ServiceProvider.GetRequiredService<IBlobStorage>(),
            NullLogger<BackupService>.Instance,
            exportScope.ServiceProvider.GetRequiredService<BHS.CRG.Application.Activity.IActivityLog>());
        var (archive, _) = await service.ExportAsync(BackupScope.Full);

        await fixture.ResetDatabaseAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(db.Constructions);
        }

        archive.Position = 0;
        using (var importScope = fixture.Services.CreateScope())
        {
            var importer = new BackupService(
                importScope.ServiceProvider.GetRequiredService<AppDbContext>(),
                importScope.ServiceProvider.GetRequiredService<IBlobStorage>(),
                NullLogger<BackupService>.Instance,
                importScope.ServiceProvider.GetRequiredService<BHS.CRG.Application.Activity.IActivityLog>());
            var report = await importer.ImportAsync(archive);
            Assert.True(report.Success);
        }
        await archive.DisposeAsync();

        using var after = fixture.Services.CreateScope();
        var restored = after.ServiceProvider.GetRequiredService<AppDbContext>();
        var construction = Assert.Single(restored.Constructions.Where(c => c.Id == id));
        Assert.Equal("Asia/Yekaterinburg", construction.TimeZoneId);
        Assert.Equal("1С", construction.ExternalSystem);
        Assert.Equal("СТР-0042", construction.ExternalCode);

        var store = after.ServiceProvider.GetRequiredService<IAppSettingsStore>();
        Assert.Equal("Europe/Moscow", await store.GetAsync(AppSettingKeys.CompanyTimeZone));
    }

    private async Task<Guid> CreateConstructionAsync(HttpClient client, string name)
    {
        var created = await client.PostAsJsonAsync("/api/constructions", new { name });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<string?> ReadTimeZoneAsync(HttpClient client, Guid id)
    {
        var construction = await ReadConstructionAsync(client, id);
        var zone = construction.GetProperty("timeZoneId");
        return zone.ValueKind == JsonValueKind.Null ? null : zone.GetString();
    }

    private static async Task<JsonElement> ReadConstructionAsync(HttpClient client, Guid id) =>
        await ReadJsonAsync(client, $"/api/constructions/{id}");

    private static async Task<JsonElement> ReadJsonAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Клиент под учётной записью администратора: права здесь не предмет проверки.</summary>
    private async Task<HttpClient> SignInAsync()
    {
        var email = $"attrs_{Guid.NewGuid():N}@test.local";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, SystemRoles.Admin)).Succeeded);
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
