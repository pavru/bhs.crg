using System.Text.Json;
using BHS.CRG.Application.Catalog;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

[Collection("Integration")]
public class CatalogHandlerTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator Mediator(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediator>();

    private static JsonDocument Json(string json) => JsonDocument.Parse(json);

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ReturnsEntityWithNewId()
    {
        using var scope = fixture.Services.CreateScope();
        var result = await Mediator(scope).Send(
            new CreateCatalogEntityCommand("Organization", "ООО Тест", Json("{}"), null));

        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal("Organization", result.EntityType);
        Assert.Equal("ООО Тест", result.DisplayName);
    }

    [Fact]
    public async Task Create_PersistsToDatabase()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(
            new CreateCatalogEntityCommand("Organization", "ООО Тест", Json("{}"), null));

        using var scope2 = fixture.Services.CreateScope();
        var fetched = await Mediator(scope2).Send(new GetCatalogEntityQuery(created.Id));

        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal("ООО Тест", fetched.DisplayName);
    }

    // ── GetById ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_ReturnsNull_WhenNotFound()
    {
        using var scope = fixture.Services.CreateScope();
        var result = await Mediator(scope).Send(new GetCatalogEntityQuery(Guid.NewGuid()));

        Assert.Null(result);
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ChangesDisplayNameAndData()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(
            new CreateCatalogEntityCommand("Organization", "Старое имя", Json("{}"), null));

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(
            new UpdateCatalogEntityCommand(created.Id, "Новое имя", Json(@"{""inn"":""1234""}")));

        Assert.Equal("Новое имя", updated.DisplayName);

        using var scope3 = fixture.Services.CreateScope();
        var fetched = await Mediator(scope3).Send(new GetCatalogEntityQuery(created.Id));
        Assert.Equal("Новое имя", fetched!.DisplayName);
    }

    [Fact]
    public async Task Update_ThrowsKeyNotFound_ForUnknownId()
    {
        using var scope = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Mediator(scope).Send(
                new UpdateCatalogEntityCommand(Guid.NewGuid(), "X", Json("{}"))));
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesEntityFromDatabase()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(
            new CreateCatalogEntityCommand("Organization", "Удаляемый", Json("{}"), null));

        using var scope2 = fixture.Services.CreateScope();
        await Mediator(scope2).Send(new DeleteCatalogEntityCommand(created.Id));

        using var scope3 = fixture.Services.CreateScope();
        var fetched = await Mediator(scope3).Send(new GetCatalogEntityQuery(created.Id));
        Assert.Null(fetched);
    }

    // ── List / filter ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_FiltersByEntityType()
    {
        using var scope = fixture.Services.CreateScope();
        var m = Mediator(scope);
        await m.Send(new CreateCatalogEntityCommand("Organization", "Орг1", Json("{}"), null));
        await m.Send(new CreateCatalogEntityCommand("Organization", "Орг2", Json("{}"), null));
        await m.Send(new CreateCatalogEntityCommand("Person", "Иванов", Json("{}"), null));

        using var scope2 = fixture.Services.CreateScope();
        var orgs = await Mediator(scope2).Send(new ListCatalogEntitiesQuery("Organization", null));
        var persons = await Mediator(scope2).Send(new ListCatalogEntitiesQuery("Person", null));
        var all = await Mediator(scope2).Send(new ListCatalogEntitiesQuery(null, null));

        Assert.Equal(2, orgs.Count);
        Assert.Single(persons);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task List_FiltersByOwnerId()
    {
        var ownerId = Guid.NewGuid();
        using var scope = fixture.Services.CreateScope();
        var m = Mediator(scope);
        await m.Send(new CreateCatalogEntityCommand("Person", "Чужой", Json("{}"), null));
        await m.Send(new CreateCatalogEntityCommand("Person", "Свой", Json("{}"), ownerId));

        using var scope2 = fixture.Services.CreateScope();
        var owned = await Mediator(scope2).Send(new ListCatalogEntitiesQuery("Person", ownerId));

        Assert.Single(owned);
        Assert.Equal("Свой", owned[0].DisplayName);
    }
}
