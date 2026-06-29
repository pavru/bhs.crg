using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

[Collection("Integration")]
public class DocumentTypeHandlerTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator Mediator(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediator>();

    private static JsonDocument EmptySchema() => JsonDocument.Parse(@"{""fields"":[]}");

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PersistsDocumentType()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("АОСР", "AOSR", DocumentTypeKind.Document, null, EmptySchema()));

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("АОСР", created.Name);
        Assert.Equal("AOSR", created.Code);
        Assert.Equal(DocumentTypeKind.Document, created.Kind);
        Assert.False(created.IsAbstract);
    }

    // ── GetById ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_ReturnsNull_WhenNotFound()
    {
        using var scope = fixture.Services.CreateScope();
        var result = await Mediator(scope).Send(new GetDocumentTypeQuery(Guid.NewGuid()));
        Assert.Null(result);
    }

    // ── UpdateSchema ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateSchema_ChangesSchemaInPlace()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Тип", "T1", DocumentTypeKind.Document, null, EmptySchema()));

        var newSchema = JsonDocument.Parse(@"{""fields"":[{""key"":""name"",""title"":""Имя"",""type"":""string""}]}");

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(
            new UpdateDocumentTypeSchemaCommand(created.Id, newSchema));

        Assert.Equal(created.Id, updated.Id);
        using var doc = updated.Schema;
        Assert.True(doc.RootElement.TryGetProperty("fields", out _));
    }

    // ── Rename / SetParent ────────────────────────────────────────────────────

    [Fact]
    public async Task Update_RenamesDocumentType()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Старое", "OLD1", DocumentTypeKind.Document, null, EmptySchema()));

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(
            new UpdateDocumentTypeCommand(created.Id, "Новое", "NEW1", null));

        Assert.Equal("Новое", updated.Name);
        Assert.Equal("NEW1", updated.Code);
        Assert.Null(updated.ParentId);
    }

    [Fact]
    public async Task Update_SetsParentId()
    {
        using var scope = fixture.Services.CreateScope();
        var parent = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Базовый", "BASE", DocumentTypeKind.Document, null, EmptySchema()));
        var child = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Дочерний", "CHILD", DocumentTypeKind.Document, null, EmptySchema()));

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(
            new UpdateDocumentTypeCommand(child.Id, "Дочерний", "CHILD", parent.Id));

        Assert.Equal(parent.Id, updated.ParentId);
    }

    // ── Cycle detection ───────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ThrowsOnCyclicParent()
    {
        using var scope = fixture.Services.CreateScope();
        var parent = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Родитель", "PAR", DocumentTypeKind.Document, null, EmptySchema()));
        var child = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Дочерний", "CHD", DocumentTypeKind.Document, parent.Id, EmptySchema()));

        using var scope2 = fixture.Services.CreateScope();
        // Trying to set child as parent of parent → cycle
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Mediator(scope2).Send(new UpdateDocumentTypeCommand(parent.Id, "Родитель", "PAR", child.Id)));
    }

    // ── SetAbstract ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAbstract_TogglesFlag()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Тип", "T2", DocumentTypeKind.Document, null, EmptySchema(), false));

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(
            new SetDocumentTypeAbstractCommand(created.Id, true));

        Assert.True(updated.IsAbstract);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_WithChildren_ThrowsInvalidOperation()
    {
        using var scope = fixture.Services.CreateScope();
        var parent = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Родитель", "P3", DocumentTypeKind.Document, null, EmptySchema()));
        await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Дочерний", "C3", DocumentTypeKind.Document, parent.Id, EmptySchema()));

        using var scope2 = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Mediator(scope2).Send(new DeleteDocumentTypeCommand(parent.Id)));
    }
}
