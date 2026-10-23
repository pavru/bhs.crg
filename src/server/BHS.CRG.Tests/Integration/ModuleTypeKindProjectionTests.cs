using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Род заведённого типа проекция не меняет — отказ, как у носителя (change <c>plan-06-estimate-model</c>):
/// молча оставленный прежний род держал бы тип не в том месте, а объявление говорило бы другое.
/// </summary>
[Collection("Integration")]
public class ModuleTypeKindProjectionTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Смена_рода_у_заведённого_типа_отказ()
    {
        var seed = new WorkTypeSeed(fixture);
        var code = $"ПробаРода{Guid.NewGuid():N}";
        ModuleTypeSpec Spec(DocumentTypeKind kind) =>
            new("probe", code, $"Проба рода {code}", SchemaEditLevel.Open, [], Kind: kind);

        var created = await seed.SendAsync(new ProjectModuleTypeCommand(Spec(DocumentTypeKind.Document)));
        Assert.Equal(DocumentTypeKind.Document, created!.Kind);
        // Повтор того же объявления — не отказ: проекция идемпотентна.
        Assert.NotNull(await seed.SendAsync(new ProjectModuleTypeCommand(Spec(DocumentTypeKind.Document))));

        var refusal = await Assert.ThrowsAsync<ConflictException>(() =>
            seed.SendAsync(new ProjectModuleTypeCommand(Spec(DocumentTypeKind.Composite))));
        Assert.Contains("род не меняет", refusal.Message);
    }
}
