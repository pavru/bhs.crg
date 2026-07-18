using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>Профиль уровня (issue #258): амбиентный инжект data.уровень.* + ленивое создание объекта.</summary>
[Collection("Integration")]
public class LevelProfileTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));
    private readonly Guid _userId = Guid.NewGuid();

    private async Task<(Guid ConstructionId, Guid SectionId, Guid SetId)> SetupAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var c = await m.Send(new CreateConstructionCommand("Стройка", _userId));
        var s = await m.Send(new CreateSectionCommand(c.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(s.Id, "Комплект"));
        return (c.Id, s.Id, set.Id);
    }

    private async Task<Guid> CompositeTypeAsync(string name, string schema)
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await scope.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new CreateDocumentTypeCommand(name, name, DocumentTypeKind.Composite, null, J(schema)));
        return dt.Id;
    }

    private async Task<Guid> CommonDataAsync(Guid typeId, string data, CatalogScope scope, Guid scopeId)
    {
        using var s = fixture.Services.CreateScope();
        var e = await s.ServiceProvider.GetRequiredService<IMediator>()
            .Send(new CreateCommonDataEntryCommand("Профиль", typeId, J(data), scope, scopeId));
        return e.Id;
    }

    private async Task<GenerationContext> ResolveDocAsync(Guid setId, Guid docTypeId, string requisites)
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var inst = await m.Send(new AddDocumentToSetCommand(setId, docTypeId));
        await m.Send(new UpdateRequisitesCommand(inst.Id, J(requisites)));
        var full = await m.Send(new GetDocumentInstanceQuery(inst.Id));
        var resolver = scope.ServiceProvider.GetRequiredService<IEntityResolver>();
        return await resolver.ResolveAsync(DocumentView.From(full!));
    }

    [Fact]
    public async Task Profile_InjectedUnderУровеньKey_WithCascadeAndEmptyForUnset()
    {
        var (cId, _, setId) = await SetupAsync();
        var profType = await CompositeTypeAsync("Профиль стройки",
            "{'tags':['profile.construction'],'fields':[{'key':'Проект','title':'Проект','type':'string'}]}");
        await CommonDataAsync(profType, "{'Проект':'ЭОМ РД'}", CatalogScope.Construction, cId);

        var docType = await CompositeTypeAsync("АОСР", "{'fields':[]}");
        var ctx = await ResolveDocAsync(setId, docType, "{'Наименование':'Акт'}");

        var уровень = (JsonElement)ctx.Data["уровень"]!;
        // Стройка — заполнена данными профиля.
        Assert.Equal("ЭОМ РД", уровень.GetProperty("стройка").GetProperty("Проект").GetString());
        // Раздел/комплект — пустые объекты (профиль-типов нет), не отсутствие ключа.
        Assert.Equal(JsonValueKind.Object, уровень.GetProperty("раздел").ValueKind);
        Assert.Equal(JsonValueKind.Object, уровень.GetProperty("комплект").ValueKind);
        Assert.Empty(уровень.GetProperty("комплект").EnumerateObject());
    }

    [Fact]
    public async Task OpeningLevelCatalog_LazilyCreatesProfileObject()
    {
        var (cId, _, _) = await SetupAsync();
        var profType = await CompositeTypeAsync("Профиль стройки", "{'tags':['profile.construction'],'fields':[]}");

        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var list = await m.Send(new ListCommonDataEntriesQuery(CatalogScope.Construction, cId, null));

        Assert.Contains(list, o => o.CompositeTypeId == profType);
        var construction = await m.Send(new GetConstructionQuery(cId));
        Assert.NotNull(construction!.ProfileObjectId);
    }
}
