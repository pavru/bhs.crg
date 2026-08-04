using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Документ из адреса обязан лежать в комплекте из того же адреса.
///
/// Проверка нужна именно сквозная, по HTTP: она живёт фильтром на группе маршрутов, и вызов
/// обработчика напрямую её не задевает — то есть тест через MediatR прошёл бы и на сломанной
/// проверке.
/// </summary>
[Collection("Integration")]
public class DocumentBelongsToSetTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private async Task<HttpClient> AuthorizedClientAsync()
    {
        var email = $"idor_{Guid.NewGuid():N}@example.com";
        const string password = "Passw0rd!Idor";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await users.CreateAsync(
                new ApplicationUser { UserName = email, Email = email, DisplayName = "Т", EmailConfirmed = true },
                password);
            Assert.True(created.Succeeded);
        }
        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task DocumentIsReachableOnlyThroughItsOwnSet()
    {
        Guid ownSetId, foreignSetId, documentId;
        using (var scope = fixture.Services.CreateScope())
        {
            var m = M(scope);
            var type = await m.Send(new CreateDocumentTypeCommand(
                "АОСР", "AOSR_IDOR", DocumentTypeKind.Document, null, J("{'fields':[]}")));
            var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
            var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
            ownSetId = (await m.Send(new CreateDocumentSetCommand(section.Id, "Свой"))).Id;
            foreignSetId = (await m.Send(new CreateDocumentSetCommand(section.Id, "Чужой"))).Id;
            documentId = (await m.Send(new AddDocumentToSetCommand(ownSetId, type.Id))).Id;
        }

        var client = await AuthorizedClientAsync();

        // Через свой комплект документ доступен — иначе тест доказывал бы лишь то, что всё сломано.
        var own = await client.GetAsync($"/api/document-sets/{ownSetId}/documents/{documentId}");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        // Через чужой — «не найден», а не «нельзя»: отдельный код подтверждал бы существование
        // документа тому, кому его не показывают.
        var foreign = await client.GetAsync($"/api/document-sets/{foreignSetId}/documents/{documentId}");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        // Несуществующий комплект отвечает так же — состав ответов не зависит от того, что есть в базе.
        var nowhere = await client.GetAsync($"/api/document-sets/{Guid.NewGuid()}/documents/{documentId}");
        Assert.Equal(HttpStatusCode.NotFound, nowhere.StatusCode);
    }

    /// <summary>
    /// Правка через чужой комплект тоже не проходит: проверка стоит на группе, а не на одном
    /// чтении, и покрывает все маршруты — включая изменяющие.
    /// </summary>
    [Fact]
    public async Task WriteThroughForeignSet_IsRefused()
    {
        Guid ownSetId, foreignSetId, documentId;
        using (var scope = fixture.Services.CreateScope())
        {
            var m = M(scope);
            var type = await m.Send(new CreateDocumentTypeCommand(
                "АОСР", "AOSR_IDOR_W", DocumentTypeKind.Document, null, J("{'fields':[]}")));
            var construction = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
            var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
            ownSetId = (await m.Send(new CreateDocumentSetCommand(section.Id, "Свой"))).Id;
            foreignSetId = (await m.Send(new CreateDocumentSetCommand(section.Id, "Чужой"))).Id;
            documentId = (await m.Send(new AddDocumentToSetCommand(ownSetId, type.Id))).Id;
        }

        var client = await AuthorizedClientAsync();

        var refused = await client.PutAsJsonAsync(
            $"/api/document-sets/{foreignSetId}/documents/{documentId}/name", new { name = "Переименован" });
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        var allowed = await client.PutAsJsonAsync(
            $"/api/document-sets/{ownSetId}/documents/{documentId}/name", new { name = "Переименован" });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }
}
