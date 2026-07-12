using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

[Collection("Integration")]
public class DocumentSetHandlerTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator Mediator(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediator>();

    private readonly Guid _userId = Guid.NewGuid();

    private async Task<(Construction construction, Section section)> CreateConstructionWithSectionAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = Mediator(scope);
        var c = await m.Send(new CreateConstructionCommand("Объект-1", _userId));
        var s = await m.Send(new CreateSectionCommand(c.Id, "Раздел-1"));
        return (c, s);
    }

    private async Task<Guid> CreateDocTypeAsync(string code)
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await Mediator(scope).Send(
            new CreateDocumentTypeCommand(code, code, DocumentTypeKind.Document,
                null, JsonDocument.Parse(@"{""fields"":[]}")));
        return dt.Id;
    }

    // ── Construction ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateConstruction_PersistsWithUserId()
    {
        using var scope = fixture.Services.CreateScope();
        var c = await Mediator(scope).Send(new CreateConstructionCommand("Объект", _userId));

        Assert.NotEqual(Guid.Empty, c.Id);
        Assert.Equal("Объект", c.Name);

        using var scope2 = fixture.Services.CreateScope();
        var fetched = await Mediator(scope2).Send(new GetConstructionQuery(c.Id));
        Assert.NotNull(fetched);
    }

    [Fact]
    public async Task RenameConstruction_UpdatesName()
    {
        using var scope = fixture.Services.CreateScope();
        var c = await Mediator(scope).Send(new CreateConstructionCommand("Старый", _userId));

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(new RenameConstructionCommand(c.Id, "Новый"));
        Assert.Equal("Новый", updated.Name);
    }

    // ── Section ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSection_ThrowsKeyNotFound_ForUnknownConstruction()
    {
        using var scope = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Mediator(scope).Send(new CreateSectionCommand(Guid.NewGuid(), "Раздел")));
    }

    // ── DocumentSet ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDocumentSet_PersistsWithSectionId()
    {
        var (_, section) = await CreateConstructionWithSectionAsync();

        using var scope = fixture.Services.CreateScope();
        var set = await Mediator(scope).Send(new CreateDocumentSetCommand(section.Id, "Комплект-1"));

        Assert.NotEqual(Guid.Empty, set.Id);
        Assert.Equal(section.Id, set.SectionId);
        Assert.Equal("Комплект-1", set.Name);
    }

    [Fact]
    public async Task RenameDocumentSet_UpdatesName()
    {
        var (_, section) = await CreateConstructionWithSectionAsync();

        using var scope = fixture.Services.CreateScope();
        var set = await Mediator(scope).Send(new CreateDocumentSetCommand(section.Id, "Старый комплект"));

        using var scope2 = fixture.Services.CreateScope();
        var renamed = await Mediator(scope2).Send(new RenameDocumentSetCommand(set.Id, "Новый комплект"));
        Assert.Equal("Новый комплект", renamed.Name);
    }

    // ── DocumentInstance ──────────────────────────────────────────────────────

    [Fact]
    public async Task AddDocumentToSet_CreatesInstance()
    {
        var (_, section) = await CreateConstructionWithSectionAsync();
        var dtId = await CreateDocTypeAsync("AOSR_INT");

        using var scope = fixture.Services.CreateScope();
        var set = await Mediator(scope).Send(new CreateDocumentSetCommand(section.Id, "Комплект"));
        var inst = await Mediator(scope).Send(new AddDocumentToSetCommand(set.Id, dtId));

        Assert.NotEqual(Guid.Empty, inst.Id);
        Assert.Equal(set.Id, inst.ScopeId);
        Assert.Equal(dtId, inst.CompositeTypeId);
    }

    [Fact]
    public async Task UpdateRequisites_PersistsJson()
    {
        var (_, section) = await CreateConstructionWithSectionAsync();
        var dtId = await CreateDocTypeAsync("AOSR_REQ");

        using var scope = fixture.Services.CreateScope();
        var set = await Mediator(scope).Send(new CreateDocumentSetCommand(section.Id, "К"));
        var inst = await Mediator(scope).Send(new AddDocumentToSetCommand(set.Id, dtId));

        var requisites = JsonDocument.Parse(@"{""Дата"":""2025-01-01"",""Номер"":""1""}");
        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(new UpdateRequisitesCommand(inst.Id, requisites));

        Assert.NotNull(updated.Data);
        using var doc = updated.Data;
        Assert.True(doc!.RootElement.TryGetProperty("Дата", out _));
    }

    [Fact]
    public async Task DeleteDocumentInstance_RemovesIt()
    {
        var (_, section) = await CreateConstructionWithSectionAsync();
        var dtId = await CreateDocTypeAsync("AOSR_DEL");

        using var scope = fixture.Services.CreateScope();
        var set = await Mediator(scope).Send(new CreateDocumentSetCommand(section.Id, "К"));
        var inst = await Mediator(scope).Send(new AddDocumentToSetCommand(set.Id, dtId));

        using var scope2 = fixture.Services.CreateScope();
        await Mediator(scope2).Send(new DeleteDocumentInstanceCommand(inst.Id));

        using var scope3 = fixture.Services.CreateScope();
        var fetched = await Mediator(scope3).Send(new GetDocumentInstanceQuery(inst.Id));
        Assert.Null(fetched);
    }

    // ── ListAvailableInstances ─────────────────────────────────────────────────

    [Fact]
    public async Task ListAvailableInstances_ReturnsAllInstancesInSameConstruction()
    {
        var (construction, section1) = await CreateConstructionWithSectionAsync();
        var dtId = await CreateDocTypeAsync("AOSR_LIST");

        using var scope = fixture.Services.CreateScope();
        var m = Mediator(scope);
        var section2 = await m.Send(new CreateSectionCommand(construction.Id, "Раздел-2"));
        var set1 = await m.Send(new CreateDocumentSetCommand(section1.Id, "К1"));
        var set2 = await m.Send(new CreateDocumentSetCommand(section2.Id, "К2"));

        await m.Send(new AddDocumentToSetCommand(set1.Id, dtId));
        await m.Send(new AddDocumentToSetCommand(set1.Id, dtId));
        await m.Send(new AddDocumentToSetCommand(set2.Id, dtId));

        using var scope2 = fixture.Services.CreateScope();
        // From set1's perspective, should see all 3 instances in the construction
        var available = await Mediator(scope2).Send(new ListAvailableInstancesQuery(set1.Id));
        Assert.Equal(3, available.Count);
    }
}
