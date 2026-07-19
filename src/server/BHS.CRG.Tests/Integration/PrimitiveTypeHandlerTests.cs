using System.Text.Json;
using BHS.CRG.Application.Catalog;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Удаление PrimitiveType (issue #269, по образцу EnumType #59): тип поля из реестра, используемый
/// в схеме какого-либо типа документа (поле type="primitive" + typeId), удалить нельзя.
/// </summary>
[Collection("Integration")]
public class PrimitiveTypeHandlerTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator Mediator(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediator>();

    private static JsonDocument Constraints(string json = "{}") => JsonDocument.Parse(json);

    [Fact]
    public async Task Delete_Unused_Succeeds()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(new CreatePrimitiveTypeCommand(
            "Инвентарный номер", "INV1", "string", null, Constraints()));

        using var scope2 = fixture.Services.CreateScope();
        await Mediator(scope2).Send(new DeletePrimitiveTypeCommand(created.Id));

        using var scope3 = fixture.Services.CreateScope();
        var all = await Mediator(scope3).Send(new ListPrimitiveTypesQuery());
        Assert.DoesNotContain(all, p => p.Id == created.Id);
    }

    [Fact]
    public async Task Delete_ReferencedInDocumentTypeSchema_ThrowsInvalidOperation()
    {
        using var scope = fixture.Services.CreateScope();
        var prim = await Mediator(scope).Send(new CreatePrimitiveTypeCommand(
            "Инвентарный номер", "INV2", "string", null, Constraints()));

        var schema = JsonDocument.Parse(
            $$"""{"fields":[{"key":"inv","type":"primitive","typeId":"{{prim.Id}}"}]}""");
        await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Акт", "ACT_PRIM", DocumentTypeKind.Document, null, schema));

        using var scope2 = fixture.Services.CreateScope();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Mediator(scope2).Send(new DeletePrimitiveTypeCommand(prim.Id)));
        Assert.Contains("Акт", ex.Message);
    }
}
