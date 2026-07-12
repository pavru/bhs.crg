using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

[Collection("Integration")]
public class CommonDataHandlerTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator Mediator(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediator>();

    private static JsonDocument Json(string json) => JsonDocument.Parse(json);
    private readonly Guid _userId = Guid.NewGuid();

    private async Task<Guid> CreateCompositeTypeAsync(string code)
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await Mediator(scope).Send(
            new CreateDocumentTypeCommand(code, code, DocumentTypeKind.Composite,
                null, JsonDocument.Parse(@"{""fields"":[]}")));
        return dt.Id;
    }

    private async Task<(Guid setId, Guid sectionId, Guid constructionId)> CreateHierarchyAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = Mediator(scope);
        var c = await m.Send(new CreateConstructionCommand("Объект", _userId));
        var s = await m.Send(new CreateSectionCommand(c.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(s.Id, "Комплект"));
        return (set.Id, s.Id, c.Id);
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PersistsEntry()
    {
        var typeId = await CreateCompositeTypeAsync("ORG_CD");

        using var scope = fixture.Services.CreateScope();
        var entry = await Mediator(scope).Send(
            new CreateCommonDataEntryCommand("ООО Тест", typeId, Json(@"{""inn"":""123""}"),
                CatalogScope.System, null));

        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal("ООО Тест", entry.DisplayName);
        Assert.Equal(CatalogScope.System, entry.Scope);
        Assert.Null(entry.ScopeId);
    }

    // ── Aliases (issue #74) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Create_NormalizesAndPersistsAliases()
    {
        var typeId = await CreateCompositeTypeAsync("ORG_AL");
        Guid id;
        using (var scope = fixture.Services.CreateScope())
        {
            var entry = await Mediator(scope).Send(new CreateCommonDataEntryCommand(
                "ООО Ромашка", typeId, Json("{}"), CatalogScope.System, null,
                new[] { "Ромашка", " Ромашка ", "ромашка", "", "  ", "РМШ" }));
            id = entry.Id;
            // trim + dedup (без учёта регистра) + отбрасывание пустых → остаются "Ромашка", "РМШ"
            Assert.Equal(new[] { "Ромашка", "РМШ" }, entry.Aliases);
        }
        using (var scope = fixture.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<CommonDataEntry>>();
            var reloaded = await repo.GetByIdAsync(id, default);
            Assert.NotNull(reloaded);
            Assert.Equal(new[] { "Ромашка", "РМШ" }, reloaded!.Aliases); // персистентно (text[])
        }
    }

    [Fact]
    public async Task Update_ReplacesAliases()
    {
        var typeId = await CreateCompositeTypeAsync("ORG_AU");
        Guid id;
        using (var scope = fixture.Services.CreateScope())
        {
            var e = await Mediator(scope).Send(new CreateCommonDataEntryCommand(
                "X", typeId, Json("{}"), CatalogScope.System, null, new[] { "старый" }));
            id = e.Id;
        }
        using (var scope = fixture.Services.CreateScope())
        {
            var e = await Mediator(scope).Send(new UpdateCommonDataEntryCommand(
                id, "X", Json("{}"), new[] { "новый1", "новый2" }));
            Assert.Equal(new[] { "новый1", "новый2" }, e.Aliases);
        }
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ChangesDisplayNameAndData()
    {
        var typeId = await CreateCompositeTypeAsync("ORG_UPD");

        using var scope = fixture.Services.CreateScope();
        var entry = await Mediator(scope).Send(
            new CreateCommonDataEntryCommand("Старое", typeId, Json("{}"), CatalogScope.System, null));

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(
            new UpdateCommonDataEntryCommand(entry.Id, "Новое", Json(@"{""inn"":""456""}")));

        Assert.Equal("Новое", updated.DisplayName);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesEntry()
    {
        var typeId = await CreateCompositeTypeAsync("ORG_DEL");

        using var scope = fixture.Services.CreateScope();
        var entry = await Mediator(scope).Send(
            new CreateCommonDataEntryCommand("Удаляемый", typeId, Json("{}"), CatalogScope.System, null));

        using var scope2 = fixture.Services.CreateScope();
        await Mediator(scope2).Send(new DeleteCommonDataEntryCommand(entry.Id));

        using var scope3 = fixture.Services.CreateScope();
        var list = await Mediator(scope3).Send(new ListCommonDataEntriesQuery());
        Assert.Empty(list);
    }

    // ── List / filter ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_FiltersByScope()
    {
        var typeId = await CreateCompositeTypeAsync("ORG_LS");
        var scopeId = Guid.NewGuid();

        using var scope = fixture.Services.CreateScope();
        var m = Mediator(scope);
        await m.Send(new CreateCommonDataEntryCommand("Системный", typeId, Json("{}"), CatalogScope.System, null));
        await m.Send(new CreateCommonDataEntryCommand("Объектный", typeId, Json("{}"), CatalogScope.Construction, scopeId));

        using var scope2 = fixture.Services.CreateScope();
        var sysOnly = await Mediator(scope2).Send(new ListCommonDataEntriesQuery(CatalogScope.System));
        var consOnly = await Mediator(scope2).Send(new ListCommonDataEntriesQuery(CatalogScope.Construction));

        Assert.Single(sysOnly);
        Assert.Single(consOnly);
        Assert.Equal("Системный", sysOnly[0].DisplayName);
    }

    [Fact]
    public async Task List_FiltersByCompositeTypeId()
    {
        var type1 = await CreateCompositeTypeAsync("CT_1");
        var type2 = await CreateCompositeTypeAsync("CT_2");

        using var scope = fixture.Services.CreateScope();
        var m = Mediator(scope);
        await m.Send(new CreateCommonDataEntryCommand("Тип1", type1, Json("{}"), CatalogScope.System, null));
        await m.Send(new CreateCommonDataEntryCommand("Тип1б", type1, Json("{}"), CatalogScope.System, null));
        await m.Send(new CreateCommonDataEntryCommand("Тип2", type2, Json("{}"), CatalogScope.System, null));

        using var scope2 = fixture.Services.CreateScope();
        var byType1 = await Mediator(scope2).Send(new ListCommonDataEntriesQuery(CompositeTypeId: type1));
        Assert.Equal(2, byType1.Count);
        Assert.All(byType1, e => Assert.Equal(type1, e.CompositeTypeId));
    }

    // ── ResolveForSet (scope hierarchy) ───────────────────────────────────────

    [Fact]
    public async Task ResolveForSet_ReturnsScopeOrderedEntries()
    {
        var typeId = await CreateCompositeTypeAsync("ORG_RS");
        var (setId, sectionId, constructionId) = await CreateHierarchyAsync();

        using var scope = fixture.Services.CreateScope();
        var m = Mediator(scope);
        // System-level entry (lowest priority)
        await m.Send(new CreateCommonDataEntryCommand("Системный", typeId, Json("{}"), CatalogScope.System, null));
        // Construction-level
        await m.Send(new CreateCommonDataEntryCommand("Объектный", typeId, Json("{}"), CatalogScope.Construction, constructionId));
        // Section-level
        await m.Send(new CreateCommonDataEntryCommand("Разделный", typeId, Json("{}"), CatalogScope.Section, sectionId));
        // Set-level (highest priority)
        await m.Send(new CreateCommonDataEntryCommand("Комплектный", typeId, Json("{}"), CatalogScope.Set, setId));
        // Unrelated scope — should be excluded
        await m.Send(new CreateCommonDataEntryCommand("Чужой", typeId, Json("{}"), CatalogScope.Set, Guid.NewGuid()));

        using var scope2 = fixture.Services.CreateScope();
        var resolved = await Mediator(scope2).Send(new ResolveCommonDataForSetQuery(setId));

        Assert.Equal(4, resolved.Count);
        // Results are ordered by priority (Set=1 first, System=5 last)
        Assert.Equal("Комплектный", resolved[0].DisplayName);
        Assert.Equal("Системный", resolved[^1].DisplayName);
    }
}
