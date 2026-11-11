using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Endpoints.Core;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Дверь сотрудников — единственная дверь записи (решение владельца по C15, 11.11.2026, change
/// <c>work-01-employee-account</c>): общий адрес справочников сотрудников не пишет, а дверь умеет всё,
/// что умел он, — подтипы, архив, вынос. Что о новом месте записи нельзя забыть, стережёт
/// <c>OwnDoorWriteInventoryTests</c>; здесь — что отказ и замена действительно работают.
/// </summary>
public partial class EmployeeDirectoryTests
{
    private static string Card(string number) => $$"""{"ТабельныйНомер":"{{number}}"}""";

    private async Task<Guid> CardAsync(Guid typeId, string name) =>
        (await SendAsync(new CreateCommonDataEntryCommand(name, typeId, JsonDocument.Parse(Card(Guid.NewGuid().ToString("N")[..8])),
            CatalogScope.System, null))).Id;

    private async Task<string> VersionAsync(Guid id) => (await SendAsync(new GetCommonDataEntryQuery(id)))!.Version;

    private static HttpRequestMessage Versioned(HttpMethod method, string url, string version, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", version);
        return request;
    }

    /// <summary>«Персона» есть, а справочника нет — причина <c>not-created</c>: выход есть, «Завести».</summary>
    [Fact]
    public async Task С_Персоной_без_справочника_причина_not_created()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            await repo.AddAsync(DocumentType.Create("Лицо", CoreRecordTypes.PersonCode, DocumentTypeKind.Composite, null,
                JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared));
            await repo.SaveChangesAsync();
        }
        var client = fixture.CreateClient();
        await AuthorizeAsync(client, CorePermissions.EmployeesRead);

        var list = await client.GetAsync("/api/employees");
        Assert.Equal(HttpStatusCode.Conflict, list.StatusCode);
        Assert.Equal("not-created", JsonDocument.Parse(await list.Content.ReadAsStringAsync())
            .RootElement.GetProperty("reason").GetString());
    }

    /// <summary>
    /// Общий адрес сотрудника не заводит, не правит, не удаляет и не отправляет в архив — 409 с адресом
    /// двери, карточка не меняется. Иначе правка реквизитов обходила бы экран «Сотрудники» правом
    /// <c>core.catalog.edit</c>. Возврат из архива общим адресом свободен — снять признак всегда благо.
    /// </summary>
    [Fact]
    public async Task Общий_адрес_сотрудника_не_пишет_и_называет_дверь()
    {
        var type = await EmployeeTypeAsync();
        var employee = await CardAsync(type.Id, "Пробнов П. П.");
        var version = await VersionAsync(employee);
        var client = fixture.CreateClient();
        await AuthorizeAsync(client, CorePermissions.CatalogRead, CorePermissions.CatalogEdit);
        client.DefaultRequestHeaders.TryAddWithoutValidation("If-Match", version);

        foreach (var refused in new[]
        {
            await client.PostAsJsonAsync("/api/common-data", new
            {
                displayName = "Новиков Н. Н.", compositeTypeId = type.Id, data = Card("9001"), scope = "System",
            }),
            await client.PutAsJsonAsync($"/api/common-data/{employee}", new { displayName = "Подмена", data = Card("0000") }),
            await client.DeleteAsync($"/api/common-data/{employee}"),
            await client.PostAsync($"/api/common-data/{employee}/archive", null),
        })
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal(EmployeeEndpoints.Door,
                (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("door").GetString());
        }

        Assert.Equal(version, await VersionAsync(employee));
        Assert.Single(await SendAsync(new ListCommonDataEntriesQuery(RecordsFor.Display, CompositeTypeId: type.Id)));
    }

    /// <summary>
    /// Архив, возврат и удаление дверью — по версии карточки: без неё 400, с устаревшей 409. Ответ архива
    /// несёт НОВУЮ версию, и возврат по ней проходит — иначе «Вернуть» тостом отказал бы на собственное
    /// действие.
    /// </summary>
    [Fact]
    public async Task Архив_возврат_и_удаление_дверью_по_версии()
    {
        var type = await EmployeeTypeAsync();
        var employee = await CardAsync(type.Id, "Архивов А. А.");
        var client = fixture.CreateClient();
        await AuthorizeAsync(client, CorePermissions.EmployeesRead, CorePermissions.EmployeesEdit);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/employees/{employee}/archive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.SendAsync(Versioned(HttpMethod.Post, $"/api/employees/{employee}/archive", "1"))).StatusCode);

        var archived = await client.SendAsync(Versioned(HttpMethod.Post, $"/api/employees/{employee}/archive",
            await VersionAsync(employee)));
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var after = JsonDocument.Parse(await archived.Content.ReadAsStringAsync()).RootElement;
        Assert.True(after.GetProperty("archived").GetBoolean());
        Assert.True((await SendAsync(new GetCommonDataEntryQuery(employee)))!.IsArchived);

        var back = await client.SendAsync(Versioned(HttpMethod.Post, $"/api/employees/{employee}/unarchive",
            after.GetProperty("version").GetString()!));
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.False((await SendAsync(new GetCommonDataEntryQuery(employee)))!.IsArchived);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync($"/api/employees/{employee}")).StatusCode);
        var deleted = await client.SendAsync(Versioned(HttpMethod.Delete, $"/api/employees/{employee}",
            await VersionAsync(employee)));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    /// <summary>
    /// Подтип «Сотрудника» — тоже сотрудник: общий адрес закрыт для всей семьи, и без двери его записи
    /// не писал бы никто. Дверь заводит запись подтипа, показывает её в списке и правит, а тип чужого
    /// справочника не принимает.
    /// </summary>
    [Fact]
    public async Task Подтип_сотрудника_пишется_и_читается_дверью()
    {
        var type = await EmployeeTypeAsync();
        DocumentType sub, alien;
        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            sub = DocumentType.Create("Монтажник", "СотрудникМонтажник", DocumentTypeKind.Composite, type.Id,
                JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared);
            alien = DocumentType.Create("Организация", "Организация", DocumentTypeKind.Composite, null,
                JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared);
            await repo.AddAsync(sub);
            await repo.AddAsync(alien);
            await repo.SaveChangesAsync();
        }
        var client = fixture.CreateClient();
        await AuthorizeAsync(client, CorePermissions.EmployeesRead, CorePermissions.EmployeesEdit);

        var created = await client.PostAsJsonAsync("/api/employees", new
        {
            displayName = "Подтипов П. П.", compositeTypeId = sub.Id, data = Card("7001"),
        });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var card = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(sub.Id, card.GetProperty("compositeTypeId").GetGuid());

        var list = JsonDocument.Parse(await client.GetStringAsync("/api/employees")).RootElement;
        Assert.Contains(list.EnumerateArray(), e => e.GetProperty("id").GetGuid() == card.GetProperty("id").GetGuid());

        var edited = await client.SendAsync(Versioned(HttpMethod.Put, $"/api/employees/{card.GetProperty("id").GetGuid()}",
            card.GetProperty("version").GetString()!, new { displayName = "Подтипов Пётр", data = Card("7001") }));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/employees", new
        {
            displayName = "Чужой", compositeTypeId = alien.Id, data = "{}",
        })).StatusCode);
    }
}
