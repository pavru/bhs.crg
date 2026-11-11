using System.Text.Json;
using BHS.CRG.Api.Auth;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сотрудник вошедшего в <c>/api/account/access</c> (ТЗ AUTH-14, WORK-47): связан, не связан,
/// справочник не заведён — три разных ответа, и клиент не выводит причину из остальных полей.
///
/// <para>Здесь, а не в <c>AccountAccessTests</c>: «справочник не заведён» требует базы без «Сотрудника»,
/// то есть очистки перед тестом, а тот класс базу не чистит нарочно — его ответы от данных не зависят.</para>
/// </summary>
public partial class EmployeeAccountLinkTests
{
    [Fact]
    public async Task Доступ_несёт_сотрудника_вошедшего_и_теряет_его_после_отвязывания()
    {
        var typeId = await DirectoryAsync();
        var employee = await EmployeeAsync(typeId, $"{_surname} Монтажник");
        var worker = fixture.CreateClient();
        var workerId = await SignInAsync(worker);
        var admin = await AdminAsync();
        await LinkOkAsync(admin, employee, workerId);

        var linked = await AccessAsync(worker);
        Assert.Equal(employee, linked.GetProperty("employee").GetProperty("id").GetGuid());
        Assert.Equal($"{_surname} Монтажник", linked.GetProperty("employee").GetProperty("name").GetString());
        Assert.False(linked.GetProperty("employee").GetProperty("archived").GetBoolean());
        Assert.Equal(JsonValueKind.Null, linked.GetProperty("employeeReason").ValueKind);

        // Отвязали во время работы — следующий же запрос без сотрудника, без перелогина.
        await UnlinkAsync(admin, employee, Version(await ReadAsync(admin, employee)));
        var unlinked = await AccessAsync(worker);
        Assert.Equal(JsonValueKind.Null, unlinked.GetProperty("employee").ValueKind);
        Assert.Equal("not-linked", unlinked.GetProperty("employeeReason").GetString());
    }

    [Fact]
    public async Task Доступ_без_справочника_называет_причину_directory_missing()
    {
        var worker = fixture.CreateClient();
        await SignInAsync(worker, CorePermissions.CatalogRead);

        var access = await AccessAsync(worker);
        Assert.Equal(JsonValueKind.Null, access.GetProperty("employee").ValueKind);
        Assert.Equal("directory-missing", access.GetProperty("employeeReason").GetString());
    }

    private static async Task<JsonElement> AccessAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/account/access");
        response.EnsureSuccessStatusCode();
        return await JsonAsync(response);
    }
}
