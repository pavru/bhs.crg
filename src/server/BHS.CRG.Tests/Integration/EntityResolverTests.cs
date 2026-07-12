using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Регрессионная сетка разбора $ref в EntityResolver + фиксация двух исправленных багов:
/// A — document-ссылка внутри массива; B — catalog-ссылка внутри таблицы подмешанного instance.
/// JSON в тестах — с одинарными кавычками (заменяются на двойные в <see cref="J"/>), чтобы не
/// экранировать кавычки и не конфликтовать с raw-string интерполяцией на }}.
/// </summary>
[Collection("Integration")]
public class EntityResolverTests(IntegrationTestFixture fixture) : IAsyncLifetime
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

    private async Task<Guid> TypeAsync(DocumentTypeKind kind, string code)
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await M(scope).Send(new CreateDocumentTypeCommand(code, code, kind, null, J("{'fields':[]}")));
        return dt.Id;
    }

    private async Task<Guid> EntryAsync(Guid typeId, string data)
    {
        using var scope = fixture.Services.CreateScope();
        var e = await M(scope).Send(new CreateCommonDataEntryCommand("Запись", typeId, J(data), CatalogScope.System, null));
        return e.Id;
    }

    private async Task<Guid> DocAsync(Guid setId, Guid typeId, string requisites)
    {
        using var scope = fixture.Services.CreateScope();
        var inst = await M(scope).Send(new AddDocumentToSetCommand(setId, typeId));
        await M(scope).Send(new UpdateRequisitesCommand(inst.Id, J(requisites)));
        return inst.Id;
    }

    private async Task<GenerationContext> ResolveAsync(Guid instanceId)
    {
        using var scope = fixture.Services.CreateScope();
        var inst = await M(scope).Send(new GetDocumentInstanceQuery(instanceId));
        var resolver = scope.ServiceProvider.GetRequiredService<IEntityResolver>();
        return await resolver.ResolveAsync(inst!);
    }

    private static JsonElement E(GenerationContext ctx, string key) => (JsonElement)ctx.Data[key]!;

    // ── Регрессия: поведение, которое должно сохраниться ─────────────────────────

    [Fact]
    public async Task Catalog_ScalarRef_Resolved()
    {
        var setId = await SetupSetAsync();
        var docType = await TypeAsync(DocumentTypeKind.Document, "DOC_A");
        var entryType = await TypeAsync(DocumentTypeKind.Composite, "ORG_A");
        var entryId = await EntryAsync(entryType, "{'Наименование':'ООО Ромашка'}");
        var docId = await DocAsync(setId, docType, "{'Орг':{'$ref':'catalog','entryId':'" + entryId + "'}}");

        var ctx = await ResolveAsync(docId);

        Assert.Equal("ООО Ромашка", E(ctx, "Орг").GetProperty("Наименование").GetString());
    }

    [Fact]
    public async Task Catalog_BaseRef_Merged()
    {
        var setId = await SetupSetAsync();
        var docType = await TypeAsync(DocumentTypeKind.Document, "DOC_B");
        var entryType = await TypeAsync(DocumentTypeKind.Composite, "ORG_B");
        var baseId = await EntryAsync(entryType, "{'Город':'Владивосток','Наименование':'База'}");
        var childId = await EntryAsync(entryType, "{'_baseRef':'" + baseId + "','Наименование':'Ромашка'}");
        var docId = await DocAsync(setId, docType, "{'Орг':{'$ref':'catalog','entryId':'" + childId + "'}}");

        var org = E(await ResolveAsync(docId), "Орг");

        Assert.Equal("Владивосток", org.GetProperty("Город").GetString());     // унаследовано
        Assert.Equal("Ромашка", org.GetProperty("Наименование").GetString());  // переопределено
    }

    // ── Базовый экземпляр документа комплекта (issue #71) ────────────────────────

    [Fact]
    public async Task InstanceBaseRef_Merged()
    {
        var setId = await SetupSetAsync();
        var baseType = await TypeAsync(DocumentTypeKind.Document, "DOC_BASE");
        var childType = await TypeAsync(DocumentTypeKind.Document, "DOC_CHILD");
        var baseId = await DocAsync(setId, baseType, "{'Город':'Владивосток','Номер':'B-1'}");
        var childId = await DocAsync(setId, childType, "{'_baseRef':'" + baseId + "','Номер':'C-1'}");

        var ctx = await ResolveAsync(childId);

        Assert.Equal("Владивосток", E(ctx, "Город").GetString()); // унаследовано от базового
        Assert.Equal("C-1", E(ctx, "Номер").GetString());         // собственное переопределяет
        Assert.False(ctx.Data.ContainsKey("_baseRef"));           // служебный ключ не протекает в контекст
    }

    [Fact]
    public async Task InstanceBaseRef_CrossSet_NotMerged()
    {
        var setId = await SetupSetAsync();
        var otherSetId = await SetupSetAsync();
        var type = await TypeAsync(DocumentTypeKind.Document, "DOC_XSET");
        var baseId = await DocAsync(otherSetId, type, "{'Город':'Владивосток'}"); // база в ДРУГОМ комплекте
        var childId = await DocAsync(setId, type, "{'_baseRef':'" + baseId + "','Номер':'C-1'}");

        var ctx = await ResolveAsync(childId);

        Assert.False(ctx.Data.ContainsKey("Город"));      // set-guard: чужой комплект не подмешивается
        Assert.Equal("C-1", E(ctx, "Номер").GetString());
    }

    [Fact]
    public async Task InstanceBaseRef_Cycle_Breaks()
    {
        var setId = await SetupSetAsync();
        var type = await TypeAsync(DocumentTypeKind.Document, "DOC_CYC");
        var aId = await DocAsync(setId, type, "{'Поле':'A'}");
        var bId = await DocAsync(setId, type, "{'_baseRef':'" + aId + "','Поле':'B'}");
        // Замыкаем цикл: A._baseRef → B, B._baseRef → A.
        using (var scope = fixture.Services.CreateScope())
            await M(scope).Send(new UpdateRequisitesCommand(aId, J("{'_baseRef':'" + bId + "','Поле':'A'}")));

        var ctx = await ResolveAsync(aId); // не должно зациклиться

        Assert.Equal("A", E(ctx, "Поле").GetString()); // собственное значение, цикл оборван через visited
    }

    [Fact]
    public async Task DocumentRef_TopLevel_Resolved()
    {
        var setId = await SetupSetAsync();
        var docType = await TypeAsync(DocumentTypeKind.Document, "DOC_C");
        var otherId = await DocAsync(setId, docType, "{'Номер':'125'}");
        var docId = await DocAsync(setId, docType,
            "{'НомерАкта':{'$ref':'document','instanceId':'" + otherId + "','fieldKey':'Номер'}}");

        Assert.Equal("125", E(await ResolveAsync(docId), "НомерАкта").GetString());
    }

    [Fact]
    public async Task InstanceRef_Depth1_Resolved_ChainStops()
    {
        var setId = await SetupSetAsync();
        var docType = await TypeAsync(DocumentTypeKind.Document, "DOC_D");
        var cId = await DocAsync(setId, docType, "{'Поле':'C-значение'}");
        var bId = await DocAsync(setId, docType,
            "{'Поле':'B-значение','Вложенный':{'$ref':'instance','instanceId':'" + cId + "'}}");
        var aId = await DocAsync(setId, docType, "{'Док':{'$ref':'instance','instanceId':'" + bId + "'}}");

        var doc = E(await ResolveAsync(aId), "Док");

        Assert.Equal("B-значение", doc.GetProperty("Поле").GetString());
        // Цепочка A→B→C: вложенная instance-ссылка НЕ разворачивается (защита от циклов) — остаётся $ref.
        Assert.Equal("instance", doc.GetProperty("Вложенный").GetProperty("$ref").GetString());
    }

    // ── Исправленные баги ────────────────────────────────────────────────────────

    [Fact] // Баг A
    public async Task DocumentRef_InsideArray_Resolved()
    {
        var setId = await SetupSetAsync();
        var docType = await TypeAsync(DocumentTypeKind.Document, "DOC_E");
        var otherId = await DocAsync(setId, docType, "{'Номер':'125'}");
        var docId = await DocAsync(setId, docType,
            "{'Список':[{'$ref':'document','instanceId':'" + otherId + "','fieldKey':'Номер'}]}");

        var arr = E(await ResolveAsync(docId), "Список");

        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal("125", arr[0].GetString()); // раньше оставалось $ref-объектом
    }

    [Fact] // Баг B
    public async Task CatalogRef_InsideTableOfInjectedInstance_Resolved()
    {
        var setId = await SetupSetAsync();
        var docType = await TypeAsync(DocumentTypeKind.Document, "DOC_F");
        var entryType = await TypeAsync(DocumentTypeKind.Composite, "ORG_F");
        var entryId = await EntryAsync(entryType, "{'Наименование':'ООО Ромашка'}");
        var bId = await DocAsync(setId, docType,
            "{'Таблица':[{'Поставщик':{'$ref':'catalog','entryId':'" + entryId + "'}}]}");
        var aId = await DocAsync(setId, docType, "{'Док':{'$ref':'instance','instanceId':'" + bId + "'}}");

        var table = E(await ResolveAsync(aId), "Док").GetProperty("Таблица");

        // раньше catalog-ссылка внутри таблицы подмешанного instance оставалась неразрешённой
        Assert.Equal("ООО Ромашка", table[0].GetProperty("Поставщик").GetProperty("Наименование").GetString());
    }
}
