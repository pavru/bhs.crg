using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Справочник сотрудников: сотрудник живёт БЕЗ учётной записи и переживает её (ТЗ CORE-7,
/// issue #962).
///
/// <para>Это сторож самой задачи. Причина, по которой справочник вообще отделён от учётных
/// записей, записана в ТЗ так: учётные записи не попадают в резервную копию и удаляются жёстко, а
/// для выгрузки в бухгалтерию нужен табельный номер. Значит проверять надо не «сотрудник
/// создался», а то, ради чего всё делалось, — что удаление учётной записи его НЕ уносит.</para>
///
/// <para>⚠️ Issue предлагала ломать этот сторож внешним ключом с каскадом. В принятом хранении
/// такого ключа не существует: сотрудник — объект общего типа, связь лежит полем в JSON, и FK
/// туда не поставить. Тест от этого не становится тавтологией — ломается он ДРУГИМ: любой
/// «уборкой» при удалении пользователя (именно так сносятся его личные уведомления — явным
/// запросом, потому что внешнего ключа нет) или очисткой поля связи.</para>
/// </summary>
[Collection("Integration")]
public class EmployeeDirectoryTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string Password = "Passw0rd!";

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    /// <summary>Справочник — как его заводит проекция при старте: от «Персоны» и по объявлению ядра.</summary>
    private async Task<DocumentType> EmployeeTypeAsync()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            var person = DocumentType.Create("Лицо", CoreRecordTypes.PersonCode,
                DocumentTypeKind.Composite, null, JsonDocument.Parse("""{"fields":[]}"""),
                Domain.Documents.TypeOwner.Core, TypeVisibility.Shared);
            await repo.AddAsync(person);
            await repo.SaveChangesAsync();
        }

        return (await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.Employee)))!;
    }

    /// <summary>
    /// Сотрудник со связанной учётной записью.
    ///
    /// <para>⚠️ Связь кладётся ЧЕРЕЗ ХРАНИЛИЩЕ, а не командой, и это не обход ради удобства:
    /// поле заперто, и охрана записи отвергает его заполнение любым обычным путём — «значение
    /// заполнено впервые, это делает модуль своей командой». Такой команды в этапе 1 нет (действие
    /// «связать» отложено), поэтому единственный способ получить связанного сотрудника — положить
    /// значение так, как положит его будущий код. Что охрана держит, проверяется отдельно.</para>
    /// </summary>
    private async Task<DomainObject> EmployeeAsync(Guid typeId, Guid accountId)
    {
        var created = await SendAsync(new CreateCommonDataEntryCommand(
            "Иванов И. И.", typeId,
            JsonDocument.Parse("""{"ТабельныйНомер":"0421"}"""),
            CatalogScope.System, null));

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
        var stored = await repo.GetByIdAsync(created.Id);
        stored!.SetData(JsonDocument.Parse(
            $$"""{"ТабельныйНомер":"0421","УчётнаяЗапись":"{{accountId}}"}"""));
        repo.Update(stored);
        await repo.SaveChangesAsync();
        return stored;
    }

    // ── Сторож задачи ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Удаление_учётной_записи_не_уносит_сотрудника()
    {
        var type = await EmployeeTypeAsync();

        Guid userId;
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            // Адрес уникальный: учётные записи между прогонами не чистятся (в резервную копию
            // они не идут и в сбросе фикстуры их нет), и постоянный адрес занял бы сам себя.
            var email = $"employee_{Guid.NewGuid():N}@test.local";
            var user = new ApplicationUser
            {
                UserName = email, Email = email, DisplayName = "Иванов", EmailConfirmed = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            userId = user.Id;
        }

        var employee = await EmployeeAsync(type.Id, userId);

        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(userId.ToString());
            Assert.True((await users.DeleteAsync(user!)).Succeeded);
        }

        var survived = await SendAsync(new GetCommonDataEntryQuery(employee.Id));

        Assert.NotNull(survived);
        // Табельный номер — то, ради чего справочник и отделён: по нему сотрудника находит
        // бухгалтерия, когда учётной записи давно нет.
        Assert.Equal("0421", survived.Data.RootElement.GetProperty("ТабельныйНомер").GetString());
        // Связь остаётся оборванной, а не вычищенной: «связи не было» и «учётную запись удалили» —
        // разные факты, и второй виден только по сохранённому идентификатору.
        Assert.Equal(userId.ToString(),
            survived.Data.RootElement.GetProperty("УчётнаяЗапись").GetString());
    }

    /// <summary>
    /// Связь с учётной записью обычным сохранением карточки не ставится — поле заперто.
    ///
    /// <para>Это единственное, что у поля связи сегодня РАБОТАЕТ, и проверять надо именно его:
    /// действия «связать» в этапе 1 нет, значение кладёт будущий код. Не будь замка, связь правил
    /// бы кто угодно с правом на карточку — то есть «учётная запись сотрудника» стала бы обычным
    /// текстовым полем, которому нельзя верить.</para>
    ///
    /// <para>Обнаружено не разбором, а тем, что охрана записи отвергла мой собственный тестовый
    /// набор данных.</para>
    /// </summary>
    [Fact]
    public async Task Связь_с_учётной_записью_обычным_сохранением_не_ставится()
    {
        var type = await EmployeeTypeAsync();

        var refused = await Assert.ThrowsAsync<Application.Schema.RecordWriteRefusedException>(() =>
            SendAsync(new CreateCommonDataEntryCommand(
                "Сидоров", type.Id,
                JsonDocument.Parse($$"""{"УчётнаяЗапись":"{{Guid.NewGuid()}}"}"""),
                CatalogScope.System, null)));

        // Отказ обязан назвать ПОЛЕ: «запись не сохранена» без адреса не говорит, что чинить.
        Assert.Contains("Учётная запись", refused.Message);
    }

    // ── Дверь справочника ─────────────────────────────────────────────────────

    /// <summary>
    /// Дверь сотрудников отдаёт и правит ТОЛЬКО сотрудников. Без проверки типа право
    /// <c>core.employees.edit</c> означало бы <c>core.catalog.edit</c>: через этот же адрес
    /// правилась бы любая запись общих данных — организации, единицы измерения, что угодно.
    /// </summary>
    [Fact]
    public async Task Чужая_запись_через_дверь_сотрудников_не_видна()
    {
        var type = await EmployeeTypeAsync();
        var employee = await EmployeeAsync(type.Id, Guid.NewGuid());

        Guid otherId;
        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            var other = DocumentType.Create("Организация", "ТестОрганизация",
                DocumentTypeKind.Composite, null, JsonDocument.Parse("""{"fields":[]}"""),
                Domain.Documents.TypeOwner.Core, TypeVisibility.Shared);
            await repo.AddAsync(other);
            await repo.SaveChangesAsync();
            otherId = other.Id;
        }
        var alien = await SendAsync(new CreateCommonDataEntryCommand(
            "ООО «Ромашка»", otherId, JsonDocument.Parse("{}"), CatalogScope.System, null));

        var client = fixture.CreateClient();
        await AuthorizeAsync(client, CorePermissions.EmployeesRead, CorePermissions.EmployeesEdit);

        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync($"/api/employees/{employee.Id}")).StatusCode);
        // Чужая карточка — 404, а не 403: существование записи за этой дверью не подтверждается.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/employees/{alien.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/employees/{alien.Id}")).StatusCode);
    }

    /// <summary>
    /// Чтение справочника правкой не заменяется, и наоборот: группы независимы, как у комплектов.
    /// </summary>
    [Fact]
    public async Task Правка_справочника_требует_своего_права()
    {
        var client = fixture.CreateClient();
        await AuthorizeAsync(client, CorePermissions.EmployeesRead);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/employees")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/api/employees", new { displayName = "Петров" })).StatusCode);
    }

    /// <summary>
    /// На установке, где справочника ещё нет, список пуст, а попытка записи отвечает ПРИЧИНОЙ.
    /// Пустой список на запись выглядел бы поломкой: «сохранил, а его нет».
    /// </summary>
    [Fact]
    public async Task Без_заведённого_справочника_список_пуст_а_запись_объясняется()
    {
        var client = fixture.CreateClient();
        await AuthorizeAsync(client, CorePermissions.EmployeesRead, CorePermissions.EmployeesEdit);

        var list = await client.GetAsync("/api/employees");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("[]", (await list.Content.ReadAsStringAsync()).Trim());

        var created = await client.PostAsJsonAsync("/api/employees", new { displayName = "Петров" });
        // 409, а не 400: запрос сам по себе правильный — не пускает состояние системы.
        Assert.Equal(HttpStatusCode.Conflict, created.StatusCode);
        Assert.Contains("Персона", await created.Content.ReadAsStringAsync());
    }

    private async Task AuthorizeAsync(HttpClient client, params string[] permissions)
    {
        var roleName = $"Emp_{Guid.NewGuid():N}";
        var email = $"emp_{Guid.NewGuid():N}@test.local";
        const string password = Password;

        using (var scope = fixture.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var role = new IdentityRole<Guid>(roleName);
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            foreach (var code in permissions)
                Assert.True((await roles.AddClaimAsync(role,
                    new System.Security.Claims.Claim(RoleSynchronizer.PermissionClaim, code))).Succeeded);

            var user = new ApplicationUser
            {
                UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true,
            };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
        }

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }
}
