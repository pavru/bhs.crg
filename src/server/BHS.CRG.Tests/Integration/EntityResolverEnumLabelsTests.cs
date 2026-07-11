using System.Text.Json;
using BHS.CRG.Application.Catalog;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// ResolveEnumLabelsAsync (issue #59): код enum-поля в реквизитах резолвится в отображаемое имя
/// EnumType перед генерацией — иначе в PDF попадёт сырой код вместо человекочитаемого текста.
/// </summary>
[Collection("Integration")]
public class EntityResolverEnumLabelsTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    private async Task<Guid> SetupSetAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var c = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var s = await m.Send(new CreateSectionCommand(c.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(s.Id, "Комплект"));
        return set.Id;
    }

    private async Task<Guid> DocAsync(Guid setId, Guid typeId, string requisites)
    {
        using var scope = fixture.Services.CreateScope();
        var inst = await M(scope).Send(new AddDocumentToSetCommand(setId, typeId));
        await M(scope).Send(new UpdateRequisitesCommand(inst.Id, J(requisites)));
        return inst.Id;
    }

    private async Task ResolveEnumLabelsAsync(GenerationContext ctx, Guid instanceId)
    {
        using var scope = fixture.Services.CreateScope();
        var inst = await M(scope).Send(new GetDocumentInstanceQuery(instanceId));
        var resolver = scope.ServiceProvider.GetRequiredService<IEntityResolver>();
        await resolver.ResolveEnumLabelsAsync(ctx, inst!, default);
    }

    [Fact]
    public async Task EnumFieldWithMatchingCode_ResolvesToLabel()
    {
        using var scope = fixture.Services.CreateScope();
        var enumType = await M(scope).Send(new CreateEnumTypeCommand(
            "Статус", "STATUS_A", null, J("[{'code':'APPROVED','label':'Согласован'}]")));
        var typeId = await M(scope).Send(new CreateDocumentTypeCommand("DOC_A", "DOC_A", DocumentTypeKind.Document, null,
            J($"{{'fields':[{{'key':'Статус','type':'enum','typeId':'{enumType.Id}'}}]}}")));
        var setId = await SetupSetAsync();
        var docId = await DocAsync(setId, typeId.Id, "{'Статус':'APPROVED'}");

        var ctx = new GenerationContext();
        ctx.Set("Статус", "APPROVED");
        await ResolveEnumLabelsAsync(ctx, docId);

        Assert.Equal("Согласован", ctx.Data["Статус"]);
    }

    [Fact]
    public async Task EnumFieldWithNonMatchingCode_LeftAsIs()
    {
        using var scope = fixture.Services.CreateScope();
        var enumType = await M(scope).Send(new CreateEnumTypeCommand(
            "Статус", "STATUS_B", null, J("[{'code':'APPROVED','label':'Согласован'}]")));
        var typeId = await M(scope).Send(new CreateDocumentTypeCommand("DOC_B", "DOC_B", DocumentTypeKind.Document, null,
            J($"{{'fields':[{{'key':'Статус','type':'enum','typeId':'{enumType.Id}'}}]}}")));
        var setId = await SetupSetAsync();
        var docId = await DocAsync(setId, typeId.Id, "{'Статус':'UNKNOWN_CODE'}");

        var ctx = new GenerationContext();
        ctx.Set("Статус", "UNKNOWN_CODE");
        await ResolveEnumLabelsAsync(ctx, docId);

        // Толерантность: код без совпадения в реестре остаётся как есть, а не пропадает/падает.
        Assert.Equal("UNKNOWN_CODE", ctx.Data["Статус"]);
    }

    [Fact]
    public async Task NonEnumField_NotTouched()
    {
        var typeId = await M(fixture.Services.CreateScope()).Send(new CreateDocumentTypeCommand("DOC_C", "DOC_C",
            DocumentTypeKind.Document, null, J("{'fields':[{'key':'Имя','type':'string'}]}")));
        var setId = await SetupSetAsync();
        var docId = await DocAsync(setId, typeId.Id, "{'Имя':'Тест'}");

        var ctx = new GenerationContext();
        ctx.Set("Имя", "Тест");
        await ResolveEnumLabelsAsync(ctx, docId);

        Assert.Equal("Тест", ctx.Data["Имя"]);
    }
}
