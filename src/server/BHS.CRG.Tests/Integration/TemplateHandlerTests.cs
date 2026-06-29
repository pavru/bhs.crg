using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

[Collection("Integration")]
public class TemplateHandlerTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator Mediator(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediator>();

    private async Task<Guid> CreateDocTypeAsync(string code)
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await Mediator(scope).Send(
            new CreateDocumentTypeCommand(code, code, DocumentTypeKind.Document,
                null, JsonDocument.Parse(@"{""fields"":[]}")));
        return dt.Id;
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PersistsTemplate_WithIsActiveTrue()
    {
        var dtId = await CreateDocTypeAsync("DT_C1");

        using var scope = fixture.Services.CreateScope();
        var t = await Mediator(scope).Send(
            new CreateTemplateCommand(dtId, "Шаблон 1", "#set(page(\"a4\"))"));

        Assert.NotEqual(Guid.Empty, t.Id);
        Assert.Equal(dtId, t.DocumentTypeId);
        Assert.Equal("Шаблон 1", t.Name);
        Assert.Equal(1, t.Version);
        Assert.True(t.IsActive);
    }

    // ── Update (versioning) ───────────────────────────────────────────────────

    [Fact]
    public async Task Update_CreatesNewVersionAndDeactivatesOld()
    {
        var dtId = await CreateDocTypeAsync("DT_U1");

        using var scope = fixture.Services.CreateScope();
        var original = await Mediator(scope).Send(
            new CreateTemplateCommand(dtId, "Шаблон", "v1 content"));

        using var scope2 = fixture.Services.CreateScope();
        var newVersion = await Mediator(scope2).Send(
            new UpdateTemplateCommand(original.Id, "v2 content"));

        Assert.Equal(2, newVersion.Version);
        Assert.True(newVersion.IsActive);
        Assert.Equal("v2 content", newVersion.Content);

        // Old version is now inactive
        using var scope3 = fixture.Services.CreateScope();
        var templates = await Mediator(scope3).Send(new ListTemplatesQuery(dtId));
        var old = templates.First(t => t.Id == original.Id);
        Assert.False(old.IsActive);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesTemplate()
    {
        var dtId = await CreateDocTypeAsync("DT_D1");

        using var scope = fixture.Services.CreateScope();
        var t = await Mediator(scope).Send(
            new CreateTemplateCommand(dtId, "Удаляемый", "content"));

        using var scope2 = fixture.Services.CreateScope();
        await Mediator(scope2).Send(new DeleteTemplateCommand(t.Id));

        using var scope3 = fixture.Services.CreateScope();
        var templates = await Mediator(scope3).Send(new ListTemplatesQuery(dtId));
        Assert.Empty(templates);
    }

    // ── GetActive ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetActive_ReturnsActiveTemplate()
    {
        var dtId = await CreateDocTypeAsync("DT_A1");

        using var scope = fixture.Services.CreateScope();
        await Mediator(scope).Send(new CreateTemplateCommand(dtId, "Шаблон", "v1"));

        using var scope2 = fixture.Services.CreateScope();
        var active = await Mediator(scope2).Send(new GetActiveTemplateQuery(dtId));

        Assert.NotNull(active);
        Assert.True(active.IsActive);
    }

    [Fact]
    public async Task GetActive_ReturnsNull_WhenNoneExist()
    {
        using var scope = fixture.Services.CreateScope();
        var active = await Mediator(scope).Send(new GetActiveTemplateQuery(Guid.NewGuid()));
        Assert.Null(active);
    }

    // ── SetDefault ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetDefault_MarksTemplateAsDefault_AndUnmarksOthers()
    {
        var dtId = await CreateDocTypeAsync("DT_DEF");

        using var scope = fixture.Services.CreateScope();
        var t1 = await Mediator(scope).Send(new CreateTemplateCommand(dtId, "T1", "v1"));
        var t2 = await Mediator(scope).Send(new CreateTemplateCommand(dtId, "T2", "v2"));

        // Make t1 default first
        using var scope2 = fixture.Services.CreateScope();
        await Mediator(scope2).Send(new SetTemplateDefaultCommand(t1.Id));

        // Now make t2 default
        using var scope3 = fixture.Services.CreateScope();
        await Mediator(scope3).Send(new SetTemplateDefaultCommand(t2.Id));

        using var scope4 = fixture.Services.CreateScope();
        var all = await Mediator(scope4).Send(new ListTemplatesQuery(dtId));
        Assert.False(all.First(t => t.Id == t1.Id).IsDefault);
        Assert.True(all.First(t => t.Id == t2.Id).IsDefault);
    }

    // ── UpdateSettings ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateSettings_ChangesPageProperties()
    {
        var dtId = await CreateDocTypeAsync("DT_S1");

        using var scope = fixture.Services.CreateScope();
        var t = await Mediator(scope).Send(new CreateTemplateCommand(dtId, "Шаблон", "v1"));

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(
            new UpdateTemplateSettingsCommand(t.Id, "A3", "landscape", 15, 10, 15, 25));

        Assert.Equal("A3", updated.PageSize);
        Assert.Equal("landscape", updated.PageOrientation);
        Assert.Equal(15, updated.MarginTop);
        Assert.Equal(25, updated.MarginLeft);
    }
}
