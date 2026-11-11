using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Связь «учётная запись → сотрудник» (ТЗ WORK-47, CORE-15, CORE-28; change <c>work-01-employee-account</c>).
///
/// <para>Сторож change — «вторая связь той же учётной записи»: на этой связи стоят «свои» отчёты
/// монтажника, и учётная запись, ставшая сотрудником дважды, отдала бы одному человеку отчёты другого.
/// Ломается он так: снят уникальный индекс или связь легла строкой в JSON карточки.</para>
///
/// <para>Учётные записи между прогонами не чистятся, поэтому имена здесь — с меткой прогона: иначе
/// «одна учётная запись с тем же ФИО» встретила бы тёзок из прошлых прогонов.</para>
/// </summary>
[Collection("Integration")]
public partial class EmployeeAccountLinkTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string Password = "Passw0rd!";

    /// <summary>Метка прогона — фамилия всех вымышленных людей этого экземпляра теста.</summary>
    private readonly string _surname = "Пробный" + Guid.NewGuid().ToString("N")[..6];

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ── Связать и отвязать ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Связал_и_отвязал_состояние_версия_и_журнал()
    {
        var typeId = await DirectoryAsync();
        var employee = await EmployeeAsync(typeId, $"{_surname} Иван Иванович");
        var account = await AccountAsync($"{_surname} И. И.");
        var admin = await AdminAsync();

        var before = await ReadAsync(admin, employee);
        Assert.Equal("none", before.GetProperty("account").GetProperty("state").GetString());

        var linked = await LinkAsync(admin, employee, account, Version(before));
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var afterLink = await JsonAsync(linked);
        Assert.Equal("linked", afterLink.GetProperty("account").GetProperty("state").GetString());
        Assert.Equal($"{_surname} И. И.", afterLink.GetProperty("account").GetProperty("name").GetString());
        // Связывание двигает версию карточки: форма, открытая до него, получит 409.
        Assert.NotEqual(Version(before), Version(afterLink));
        Assert.Equal(Version(afterLink), Version(await ReadAsync(admin, employee)));

        var unlinked = await UnlinkAsync(admin, employee, Version(afterLink));
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        Assert.Equal("none", (await JsonAsync(unlinked)).GetProperty("account").GetProperty("state").GetString());

        var journal = await JournalAsync(employee);
        Assert.Equal([ActivityActions.EmployeeAccountLinked.Code, ActivityActions.EmployeeAccountUnlinked.Code],
            journal.Select(r => r.Action));
        Assert.All(journal, r => Assert.NotNull(r.ActorId));
        Assert.Equal($"{_surname} И. И.", journal[0].After);
        Assert.Equal($"{_surname} И. И.", journal[1].Before);
    }

    [Fact]
    public async Task Без_версии_400_с_устаревшей_409_связь_не_меняется()
    {
        var typeId = await DirectoryAsync();
        var employee = await EmployeeAsync(typeId, $"{_surname} Пётр Петрович");
        var account = await AccountAsync($"{_surname} П. П.");
        var admin = await AdminAsync();
        var version = Version(await ReadAsync(admin, employee));

        var noHeader = await admin.PutAsJsonAsync($"/api/employees/{employee}/account", new { userId = account });
        Assert.Equal(HttpStatusCode.BadRequest, noHeader.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await LinkAsync(admin, employee, account, "*")).StatusCode);

        var stale = await LinkAsync(admin, employee, account, "1");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("stale", (await JsonAsync(stale)).GetProperty("code").GetString());

        Assert.Equal("none", (await ReadAsync(admin, employee)).GetProperty("account").GetProperty("state").GetString());
        Assert.Equal(version, Version(await ReadAsync(admin, employee)));
    }

    [Fact]
    public async Task Без_права_правки_адреса_связи_закрыты()
    {
        var typeId = await DirectoryAsync();
        var employee = await EmployeeAsync(typeId, $"{_surname} Олег Олегович");
        var account = await AccountAsync($"{_surname} О. О.");
        var reader = fixture.CreateClient();
        await SignInAsync(reader, CorePermissions.EmployeesRead);
        var version = Version(await ReadAsync(reader, employee));

        Assert.Equal(HttpStatusCode.Forbidden, (await LinkAsync(reader, employee, account, version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UnlinkAsync(reader, employee, version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/employees/account-candidates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/employees/account-matches")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/employees/account-links",
            new { links = new[] { new { employeeId = employee, userId = account, version } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/employees/directory", null)).StatusCode);
    }

    // ── Один к одному ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Вторая_связь_той_же_учётной_записи_409_с_именем_занятого()
    {
        var typeId = await DirectoryAsync();
        var first = await EmployeeAsync(typeId, $"{_surname} Анна Андреевна");
        var second = await EmployeeAsync(typeId, $"{_surname} Борис Борисович");
        var account = await AccountAsync($"{_surname} А. А.");
        var admin = await AdminAsync();
        await LinkOkAsync(admin, first, account);

        var refused = await LinkAsync(admin, second, account, Version(await ReadAsync(admin, second)));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await JsonAsync(refused);
        Assert.Equal("taken", body.GetProperty("code").GetString());
        Assert.Contains($"{_surname} Анна Андреевна", body.GetProperty("error").GetString());
        Assert.Equal(first, body.GetProperty("linkedTo").GetProperty("id").GetGuid());
        Assert.Equal("none", (await ReadAsync(admin, second)).GetProperty("account").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Связанного_сотрудника_с_другой_учётной_записью_409_с_её_именем()
    {
        var typeId = await DirectoryAsync();
        var employee = await EmployeeAsync(typeId, $"{_surname} Вера Викторовна");
        var current = await AccountAsync($"{_surname} В. В.");
        var other = await AccountAsync($"{_surname} Другая");
        var admin = await AdminAsync();
        await LinkOkAsync(admin, employee, current);

        var refused = await LinkAsync(admin, employee, other, Version(await ReadAsync(admin, employee)));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var body = await JsonAsync(refused);
        Assert.Equal("employee-linked", body.GetProperty("code").GetString());
        Assert.Contains($"{_surname} В. В.", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// Уникальность держит БАЗА, а не проверка в службе: два связывания, пришедшие почти разом, оба
    /// прошли бы «свободна ли». Тест кладёт дубль мимо службы — и краснеет, если индекс снять.
    /// </summary>
    [Fact]
    public async Task Уникальность_учётной_записи_держит_индекс_базы()
    {
        var typeId = await DirectoryAsync();
        var first = await EmployeeAsync(typeId, $"{_surname} Первый");
        var second = await EmployeeAsync(typeId, $"{_surname} Второй");
        var account = await AccountAsync($"{_surname} Общая");

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.EmployeeAccounts.Add(EmployeeAccount.Link(first, account, "x"));
        db.EmployeeAccounts.Add(EmployeeAccount.Link(second, account, "x"));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(DbFailure.IsUniqueViolation(ex), ex.InnerException?.Message);
    }

    [Fact]
    public async Task Архивного_не_связать_а_отвязать_несвязанного_безобидно()
    {
        var typeId = await DirectoryAsync();
        var employee = await EmployeeAsync(typeId, $"{_surname} Глеб Глебович");
        var account = await AccountAsync($"{_surname} Г. Г.");
        var admin = await AdminAsync();

        var unlinked = await UnlinkAsync(admin, employee, Version(await ReadAsync(admin, employee)));
        Assert.Equal(HttpStatusCode.OK, unlinked.StatusCode);
        Assert.Empty(await JournalAsync(employee));

        await SendAsync(new SetRecordArchiveCommand(employee, true));
        var refused = await LinkAsync(admin, employee, account, Version(await ReadAsync(admin, employee)));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("archived", (await JsonAsync(refused)).GetProperty("code").GetString());
    }

    /// <summary>
    /// Источник связи — таблица, а не поле карточки: значение в запертом поле «УчётнаяЗапись» (положенное
    /// мимо охраны — обычным сохранением его не поставить) связью не становится.
    /// </summary>
    [Fact]
    public async Task Значение_в_поле_карточки_связью_не_становится()
    {
        var typeId = await DirectoryAsync();
        var account = await AccountAsync($"{_surname} Поле");
        var employee = await EmployeeAsync(typeId, $"{_surname} Поле");
        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<Domain.Objects.DomainObject>>();
            var stored = (await repo.GetByIdAsync(employee))!;
            stored.SetData(JsonDocument.Parse($$"""{"ТабельныйНомер":"0001","УчётнаяЗапись":"{{account}}"}"""));
            repo.Update(stored);
            await repo.SaveChangesAsync();
        }
        var admin = await AdminAsync();

        Assert.Equal("none", (await ReadAsync(admin, employee)).GetProperty("account").GetProperty("state").GetString());
        var candidate = (await JsonAsync(await admin.GetAsync(
                $"/api/employees/account-candidates?q={Uri.EscapeDataString(_surname + " Поле")}")))
            .GetProperty("items").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, candidate.GetProperty("linkedTo").ValueKind);
    }

    // ── Удалённая учётная запись ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Удаление_учётной_записи_даёт_deleted_с_именем_а_отвязывание_none()
    {
        var typeId = await DirectoryAsync();
        var employee = await EmployeeAsync(typeId, $"{_surname} Дарья Денисовна");
        var account = await AccountAsync($"{_surname} Д. Д.");
        var admin = fixture.CreateClient();
        await SignInAsync(admin, CorePermissions.EmployeesRead, CorePermissions.EmployeesEdit, CorePermissions.UsersManage);
        await LinkOkAsync(admin, employee, account);

        // Через НАСТОЯЩУЮ дверь удаления: правило держит внешний ключ, и сторож обязан пройти тем же путём.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{account}")).StatusCode);

        var after = await ReadAsync(admin, employee);
        var state = after.GetProperty("account");
        Assert.Equal("deleted", state.GetProperty("state").GetString());
        Assert.Equal($"{_surname} Д. Д.", state.GetProperty("name").GetString());

        // Поверх удалённой — можно связать сразу: занимать там некого.
        var fresh = await AccountAsync($"{_surname} Д. Д. новая");
        var relinked = await LinkAsync(admin, employee, fresh, Version(after));
        Assert.Equal(HttpStatusCode.OK, relinked.StatusCode);
        var journal = await JournalAsync(employee);
        Assert.Equal($"{_surname} Д. Д.", journal[^1].Before);

        var unlinked = await UnlinkAsync(admin, employee, Version(await JsonAsync(relinked)));
        var none = (await JsonAsync(unlinked)).GetProperty("account");
        Assert.Equal("none", none.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, none.GetProperty("name").ValueKind);
    }

    // ── Обвязка ───────────────────────────────────────────────────────────────────────────

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    /// <summary>Справочник — как его заводит проекция при старте: от «Персоны» и по объявлению ядра.</summary>
    private async Task<Guid> DirectoryAsync()
    {
        await PersonAsync();
        return (await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.Employee)))!.Id;
    }

    private async Task PersonAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        await repo.AddAsync(DocumentType.Create("Лицо", CoreRecordTypes.PersonCode,
            DocumentTypeKind.Composite, null, JsonDocument.Parse("""{"fields":[]}"""),
            TypeOwner.Core, TypeVisibility.Shared));
        await repo.SaveChangesAsync();
    }

    private async Task<Guid> EmployeeAsync(Guid typeId, string name, string extra = "") =>
        (await SendAsync(new CreateCommonDataEntryCommand(name, typeId,
            JsonDocument.Parse($$"""{"ТабельныйНомер":"{{Guid.NewGuid():N}}"{{extra}}}"""),
            CatalogScope.System, null, CreateAnyway: true))).Id;

    /// <summary>Вымышленная учётная запись с уникальным адресом: учётные записи между прогонами не чистятся.</summary>
    private async Task<Guid> AccountAsync(string displayName)
    {
        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"acc_{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        return user.Id;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = fixture.CreateClient();
        await SignInAsync(client, CorePermissions.EmployeesRead, CorePermissions.EmployeesEdit);
        return client;
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, Guid employee)
    {
        var response = await client.GetAsync($"/api/employees/{employee}");
        response.EnsureSuccessStatusCode();
        return await JsonAsync(response);
    }

    private static string Version(JsonElement employee) => employee.GetProperty("version").GetString()!;

    private static async Task<HttpResponseMessage> LinkAsync(HttpClient client, Guid employee, Guid account, string version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/employees/{employee}/account")
        {
            Content = JsonContent.Create(new { userId = account }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", version);
        return await client.SendAsync(request);
    }

    private static async Task LinkOkAsync(HttpClient client, Guid employee, Guid account)
    {
        var response = await LinkAsync(client, employee, account, Version(await ReadAsync(client, employee)));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> UnlinkAsync(HttpClient client, Guid employee, string version)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/employees/{employee}/account");
        request.Headers.TryAddWithoutValidation("If-Match", version);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<List<Domain.Activity.ActivityRecord>> JournalAsync(Guid employee)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var target = employee.ToString();
        return await db.Set<Domain.Activity.ActivityRecord>().AsNoTracking()
            .Where(r => r.TargetId == target).OrderBy(r => r.OccurredAt).ToListAsync();
    }

    /// <returns>Учётная запись вошедшего.</returns>
    private async Task<Guid> SignInAsync(HttpClient client, params string[] permissions)
    {
        Guid userId;
        var roleName = $"EmpAcc_{Guid.NewGuid():N}";
        var email = $"empacc_{Guid.NewGuid():N}@test.local";
        using (var scope = fixture.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var role = new IdentityRole<Guid>(roleName);
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            foreach (var code in permissions)
                Assert.True((await roles.AddClaimAsync(role,
                    new System.Security.Claims.Claim(RoleSynchronizer.PermissionClaim, code))).Succeeded);
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
            userId = user.Id;
        }

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return userId;
    }
}
