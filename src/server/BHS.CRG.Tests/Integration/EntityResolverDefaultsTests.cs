using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// ApplyDefaultsAsync (issue #53): резолвер заполняет поле defaultValue из схемы типа, только если
/// поле НЕ определено в контексте (ни реквизитами инстанса, ни биндингом) — приоритет: инстанс/биндинг
/// (уже в ctx к моменту вызова) > defaultValue схемы > пусто.
/// </summary>
[Collection("Integration")]
public class EntityResolverDefaultsTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));
    private readonly Guid _userId = Guid.NewGuid();

    private async Task<Guid> SetupSetAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = M(scope);
        var c = await m.Send(new CreateConstructionCommand("Объект", _userId));
        var s = await m.Send(new CreateSectionCommand(c.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(s.Id, "Комплект"));
        return set.Id;
    }

    private async Task<Guid> TypeAsync(string code, string schema, Guid? parentId = null)
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await M(scope).Send(new CreateDocumentTypeCommand(code, code, DocumentTypeKind.Document, parentId, J(schema)));
        return dt.Id;
    }

    private async Task<Guid> DocAsync(Guid setId, Guid typeId, string requisites = "{}")
    {
        using var scope = fixture.Services.CreateScope();
        var inst = await M(scope).Send(new AddDocumentToSetCommand(setId, typeId));
        await M(scope).Send(new UpdateRequisitesCommand(inst.Id, J(requisites)));
        return inst.Id;
    }

    private async Task ApplyDefaultsAsync(GenerationContext ctx, Guid instanceId)
    {
        using var scope = fixture.Services.CreateScope();
        var inst = await M(scope).Send(new GetDocumentInstanceQuery(instanceId));
        var resolver = scope.ServiceProvider.GetRequiredService<IEntityResolver>();
        await resolver.ApplyDefaultsAsync(ctx, DocumentView.From(inst!), default);
    }

    [Fact]
    public async Task MissingField_GetsDefaultFromSchema()
    {
        var setId = await SetupSetAsync();
        var typeId = await TypeAsync("DFLT_A",
            "{'fields':[{'key':'Статус','type':'string','required':false,'defaultValue':'Черновик'}]}");
        var docId = await DocAsync(setId, typeId);

        var ctx = new GenerationContext();
        await ApplyDefaultsAsync(ctx, docId);

        Assert.True(ctx.Data.ContainsKey("Статус"));
        Assert.Equal("Черновик", ((JsonElement)ctx.Data["Статус"]!).GetString());
    }

    [Fact]
    public async Task PresentField_KeepsExistingValue_DefaultNotApplied()
    {
        var setId = await SetupSetAsync();
        var typeId = await TypeAsync("DFLT_B",
            "{'fields':[{'key':'Статус','type':'string','required':false,'defaultValue':'Черновик'}]}");
        var docId = await DocAsync(setId, typeId);

        // Значение уже в контексте — как если бы его дал инстанс ИЛИ биндинг (ApplyDefaultsAsync
        // не различает источник, только присутствие ключа).
        var ctx = new GenerationContext();
        ctx.Set("Статус", "Утверждён");
        await ApplyDefaultsAsync(ctx, docId);

        Assert.Equal("Утверждён", ctx.Data["Статус"]);
    }

    [Fact]
    public async Task FieldWithoutDefaultValue_StaysAbsent()
    {
        var setId = await SetupSetAsync();
        var typeId = await TypeAsync("DFLT_C", "{'fields':[{'key':'БезДефолта','type':'string','required':false}]}");
        var docId = await DocAsync(setId, typeId);

        var ctx = new GenerationContext();
        await ApplyDefaultsAsync(ctx, docId);

        Assert.False(ctx.Data.ContainsKey("БезДефолта"));
    }

    [Fact]
    public async Task DerivedType_FieldOverride_WinsOverBaseDefault()
    {
        var setId = await SetupSetAsync();
        var baseId = await TypeAsync("DFLT_BASE",
            "{'fields':[{'key':'Статус','type':'string','required':false,'defaultValue':'БазовыйДефолт'}]}");
        var derivedId = await TypeAsync("DFLT_DERIVED",
            "{'fields':[],'fieldOverrides':{'Статус':{'defaultValue':'ДочернийДефолт'}}}", parentId: baseId);
        var docId = await DocAsync(setId, derivedId);

        var ctx = new GenerationContext();
        await ApplyDefaultsAsync(ctx, docId);

        Assert.Equal("ДочернийДефолт", ((JsonElement)ctx.Data["Статус"]!).GetString());
    }

    [Fact]
    public async Task NonScalarField_DefaultNotApplied()
    {
        var setId = await SetupSetAsync();
        // Составное поле с (некорректно, но защитно) заданным defaultValue в схеме — не должно
        // автоматически заполняться (материализация/биндинг составных полей — отдельная забота).
        var typeId = await TypeAsync("DFLT_D",
            "{'fields':[{'key':'Орг','type':'complex','required':false,'defaultValue':'что-то'}]}");
        var docId = await DocAsync(setId, typeId);

        var ctx = new GenerationContext();
        await ApplyDefaultsAsync(ctx, docId);

        Assert.False(ctx.Data.ContainsKey("Орг"));
    }
}
