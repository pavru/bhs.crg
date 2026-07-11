using System.Text.Json;
using BHS.CRG.Application.Catalog;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// CRUD для EnumType (issue #59) — по образцу DocumentTypeHandlerTests (issue #57): проверка
/// использования перед удалением (тип перечисления, используемый в схеме, удалить нельзя).
/// </summary>
[Collection("Integration")]
public class EnumTypeHandlerTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator Mediator(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediator>();

    private static JsonDocument Values(string json) => JsonDocument.Parse(json);

    [Fact]
    public async Task Create_PersistsEnumType()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(new CreateEnumTypeCommand(
            "Статус", "STATUS1", null, Values("""[{"code":"DRAFT","label":"Черновик"}]""")));

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("Статус", created.Name);
        Assert.Equal("STATUS1", created.Code);
    }

    [Fact]
    public async Task Create_ThrowsOnDuplicateCode()
    {
        using var scope = fixture.Services.CreateScope();
        await Mediator(scope).Send(new CreateEnumTypeCommand("Статус", "DUPE1", null, Values("[]")));

        using var scope2 = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Mediator(scope2).Send(new CreateEnumTypeCommand("Другой статус", "dupe1", null, Values("[]"))));
    }

    [Fact]
    public async Task Update_ChangesValues()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(new CreateEnumTypeCommand(
            "Статус", "UPD1", null, Values("""[{"code":"DRAFT","label":"Черновик"}]""")));

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(new UpdateEnumTypeCommand(
            created.Id, "Статус", "UPD1", "описание",
            Values("""[{"code":"DRAFT","label":"Черновик"},{"code":"APPROVED","label":"Согласован"}]""")));

        using var doc = updated.Values;
        Assert.Equal(2, doc.RootElement.GetArrayLength());
        Assert.Equal("описание", updated.Description);
    }

    [Fact]
    public async Task SetGroup_UpdatesGroup()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(new CreateEnumTypeCommand("Статус", "GRP1", null, Values("[]")));

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(new SetEnumTypeGroupCommand(created.Id, "Общие"));

        Assert.Equal("Общие", updated.Group);
    }

    [Fact]
    public async Task Delete_Unused_Succeeds()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(new CreateEnumTypeCommand("Статус", "DEL1", null, Values("[]")));

        using var scope2 = fixture.Services.CreateScope();
        await Mediator(scope2).Send(new DeleteEnumTypeCommand(created.Id));

        using var scope3 = fixture.Services.CreateScope();
        var all = await Mediator(scope3).Send(new ListEnumTypesQuery());
        Assert.DoesNotContain(all, e => e.Id == created.Id);
    }

    [Fact]
    public async Task Delete_ReferencedInDocumentTypeSchema_ThrowsInvalidOperation()
    {
        using var scope = fixture.Services.CreateScope();
        var enumType = await Mediator(scope).Send(new CreateEnumTypeCommand(
            "Статус", "DEL2", null, Values("""[{"code":"DRAFT","label":"Черновик"}]""")));

        var schema = JsonDocument.Parse(
            $$"""{"fields":[{"key":"status","type":"enum","typeId":"{{enumType.Id}}"}]}""");
        await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Акт", "ACT1", DocumentTypeKind.Document, null, schema));

        using var scope2 = fixture.Services.CreateScope();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Mediator(scope2).Send(new DeleteEnumTypeCommand(enumType.Id)));
        Assert.Contains("Акт", ex.Message);
    }
}
