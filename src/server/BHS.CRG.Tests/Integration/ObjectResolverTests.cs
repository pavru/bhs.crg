using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Resolution;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Единый резолвер «строка→объект» (issue #183, Фаза 2): стратегии Field/Name/IdentityKey,
/// scope-приоритет (узкий побеждает), включение подтипов, составной ключ (порядок схемы, строгий
/// пропуск при пустом компоненте), каноническая нормализация. Только поиск существующего (find-only).
/// </summary>
[Collection("Integration")]
public class ObjectResolverTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private IObjectResolver R(IServiceScope s) => s.ServiceProvider.GetRequiredService<IObjectResolver>();
    private static JsonDocument J(string singleQuoted) => JsonDocument.Parse(singleQuoted.Replace('\'', '"'));

    // Тип «материал» с двумя identity-полями (порядок схемы: Артикул, Наименование).
    private const string IdentitySchema =
        "{'fields':[{'key':'Артикул','type':'string','tags':['identity']},{'key':'Наименование','type':'string','tags':['identity']}]}";

    private async Task<Guid> TypeAsync(IMediator m, string code, string schema, Guid? parentId = null) =>
        (await m.Send(new CreateDocumentTypeCommand(code, code, DocumentTypeKind.Composite, parentId, J(schema)))).Id;

    private async Task<Guid> ObjAsync(IMediator m, string name, Guid typeId, string data,
        CatalogScope scope = CatalogScope.System, Guid? scopeId = null, string[]? aliases = null) =>
        (await m.Send(new CreateCommonDataEntryCommand(name, typeId, J(data), scope, scopeId, aliases))).Id;

    [Fact]
    public async Task Field_MatchesBySpecificField_Normalized()
    {
        using var s = fixture.Services.CreateScope();
        var m = M(s);
        var typeId = await TypeAsync(m, "MAT_F", IdentitySchema);
        var id = await ObjAsync(m, "Кабель ВВГ", typeId, "{'Артикул':'ВВГ-3х2.5','Наименование':'Кабель'}");

        // «шт.»-подобная нормализация: хвостовой пробел + регистр игнорируются.
        var found = await R(s).ResolveAsync(
            ObjectMatchRequest.ByField(typeId, "Артикул", " ввг-3х2.5 "), CatalogScope.System, null);
        Assert.Equal(id, found);

        Assert.Null(await R(s).ResolveAsync(
            ObjectMatchRequest.ByField(typeId, "Артикул", "нет такого"), CatalogScope.System, null));
    }

    [Fact]
    public async Task Name_MatchesDisplayNameOrAlias()
    {
        using var s = fixture.Services.CreateScope();
        var m = M(s);
        var typeId = await TypeAsync(m, "MAT_N", IdentitySchema);
        var id = await ObjAsync(m, "Кабель ВВГ", typeId, "{'Артикул':'A1'}", aliases: new[] { "ВВГ", "провод силовой" });

        Assert.Equal(id, await R(s).ResolveAsync(ObjectMatchRequest.ByName(typeId, "кабель ввг"), CatalogScope.System, null));
        Assert.Equal(id, await R(s).ResolveAsync(ObjectMatchRequest.ByName(typeId, "  ВВГ "), CatalogScope.System, null));
        Assert.Equal(id, await R(s).ResolveAsync(ObjectMatchRequest.ByName(typeId, "Провод Силовой"), CatalogScope.System, null));
        Assert.Null(await R(s).ResolveAsync(ObjectMatchRequest.ByName(typeId, "неизвестно"), CatalogScope.System, null));
    }

    [Fact]
    public async Task IdentityKey_CompositeMatch_SchemaOrder()
    {
        using var s = fixture.Services.CreateScope();
        var m = M(s);
        var typeId = await TypeAsync(m, "MAT_ID", IdentitySchema);
        var id = await ObjAsync(m, "Кабель", typeId, "{'Артикул':'ВВГ-3х2.5','Наименование':'Кабель ВВГ'}");

        var fields = new Dictionary<string, string?> { ["Артикул"] = "ВВГ-3х2.5", ["Наименование"] = "кабель ввг" };
        Assert.Equal(id, await R(s).ResolveAsync(ObjectMatchRequest.ByIdentity(typeId, fields), CatalogScope.System, null));

        // Не тот второй компонент → нет совпадения (AND-композит).
        var wrong = new Dictionary<string, string?> { ["Артикул"] = "ВВГ-3х2.5", ["Наименование"] = "другое" };
        Assert.Null(await R(s).ResolveAsync(ObjectMatchRequest.ByIdentity(typeId, wrong), CatalogScope.System, null));
    }

    [Fact]
    public async Task IdentityKey_EmptyComponent_NoKey_NoMatch()
    {
        using var s = fixture.Services.CreateScope();
        var m = M(s);
        var typeId = await TypeAsync(m, "MAT_EMPTY", IdentitySchema);
        // У объекта пустое второе identity-поле → составной ключ не строится, объект не в identity-индексе.
        await ObjAsync(m, "Частичный", typeId, "{'Артикул':'ВВГ','Наименование':''}");

        var fields = new Dictionary<string, string?> { ["Артикул"] = "ВВГ", ["Наименование"] = "" };
        Assert.Null(await R(s).ResolveAsync(ObjectMatchRequest.ByIdentity(typeId, fields), CatalogScope.System, null));
    }

    [Fact]
    public async Task ScopePriority_NarrowerWins()
    {
        using var s = fixture.Services.CreateScope();
        var m = M(s);
        var c = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var sec = await m.Send(new CreateSectionCommand(c.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(sec.Id, "Комплект"));
        var typeId = await TypeAsync(m, "MAT_SCOPE", IdentitySchema);

        var systemId = await ObjAsync(m, "Кабель", typeId, "{'Артикул':'A1'}", CatalogScope.System, null);
        var setId = await ObjAsync(m, "Кабель", typeId, "{'Артикул':'A1'}", CatalogScope.Set, set.Id);

        // Резолв из scope комплекта: узкий (Set) побеждает System.
        Assert.Equal(setId, await R(s).ResolveAsync(
            ObjectMatchRequest.ByField(typeId, "Артикул", "A1"), CatalogScope.Set, set.Id));
        // Резолв из System-scope: виден только System-объект.
        Assert.Equal(systemId, await R(s).ResolveAsync(
            ObjectMatchRequest.ByField(typeId, "Артикул", "A1"), CatalogScope.System, null));
    }

    [Fact]
    public async Task Batch_ReturnsResultsInOrder_WithNameAndNulls()
    {
        using var s = fixture.Services.CreateScope();
        var m = M(s);
        var typeId = await TypeAsync(m, "MAT_BATCH", IdentitySchema);
        var id = await ObjAsync(m, "Кабель ВВГ", typeId, "{'Артикул':'A1'}", aliases: new[] { "ВВГ" });

        var items = new List<ObjectResolveItem>
        {
            new(typeId, ObjectMatchStrategy.Name, "кабель ввг"),   // → displayName-матч
            new(typeId, ObjectMatchStrategy.Name, "нет такого"),   // → null
            new(typeId, ObjectMatchStrategy.Field, "A1", "Артикул"), // → field-матч
        };
        var res = await m.Send(new ResolveObjectsBatchQuery(CatalogScope.System, null, items));

        Assert.Equal(3, res.Count);
        Assert.NotNull(res[0]);
        Assert.Equal(id, res[0]!.EntryId);
        Assert.Equal("Кабель ВВГ", res[0]!.DisplayName);
        Assert.Equal(CatalogScope.System, res[0]!.Scope);
        Assert.Null(res[1]);
        Assert.Equal(id, res[2]!.EntryId);
    }

    [Fact]
    public async Task Subtypes_ResolvedByParentType()
    {
        using var s = fixture.Services.CreateScope();
        var m = M(s);
        var parentId = await TypeAsync(m, "MAT_PARENT", IdentitySchema);
        var childId = await TypeAsync(m, "MAT_CHILD", "{'fields':[]}", parentId);
        var obj = await ObjAsync(m, "Дочерний Кабель", childId, "{'Артикул':'A1','Наименование':'Кабель'}");

        // Поиск по РОДИТЕЛЬСКОМУ типу находит объект подтипа (кандидаты = тип + подтипы).
        Assert.Equal(obj, await R(s).ResolveAsync(
            ObjectMatchRequest.ByName(parentId, "дочерний кабель"), CatalogScope.System, null));
        var fields = new Dictionary<string, string?> { ["Артикул"] = "A1", ["Наименование"] = "Кабель" };
        Assert.Equal(obj, await R(s).ResolveAsync(
            ObjectMatchRequest.ByIdentity(parentId, fields), CatalogScope.System, null));
    }
}
