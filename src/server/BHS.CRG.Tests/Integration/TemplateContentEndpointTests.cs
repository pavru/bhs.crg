using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Tests.Support;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Список шаблонов без текстов, текст одной версии — своим адресом (issue #1972). Прежде список
/// отдавал Typst каждой сохранённой версии, и ответ рос с каждым сохранением.
/// </summary>
[Collection("Integration")]
public class TemplateContentEndpointTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    [Fact]
    public async Task Список_без_текстов_текст_версии_отдельным_адресом()
    {
        var type = (await SendAsync(new CreateDocumentTypeCommand("TPL_TXT", "TPL_TXT", DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}""")))).Id;
        var v1 = await SendAsync(new CreateTemplateCommand(type, "Основной", "= первая версия"));
        var v2 = (await SendAsync(new UpdateTemplateCommand(v1.Id, "= вторая версия"))).Template;
        // Чтения достаточно: текст нужен и тем, кто шаблоны только смотрит.
        var client = await GrantedSignIn.WithPermissionsAsync(fixture, "id.document.read");

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/templates?documentTypeId={type}");
        Assert.Equal(2, list.GetArrayLength());
        foreach (var row in list.EnumerateArray())
        {
            Assert.False(row.TryGetProperty("content", out _), $"В списке текст версии: {row}");
            Assert.True(row.TryGetProperty("isActive", out _));
        }
        // Порядок прежний — от новой версии к старой.
        Assert.Equal([v2.Id, v1.Id], list.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()));

        var historical = await client.GetFromJsonAsync<JsonElement>($"/api/templates/{v1.Id}/content");
        Assert.Contains("= первая версия", historical.GetProperty("content").GetString());
        var current = await client.GetFromJsonAsync<JsonElement>($"/api/templates/{v2.Id}/content");
        Assert.Contains("= вторая версия", current.GetProperty("content").GetString());

        var missing = await client.GetAsync($"/api/templates/{Guid.NewGuid()}/content");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
