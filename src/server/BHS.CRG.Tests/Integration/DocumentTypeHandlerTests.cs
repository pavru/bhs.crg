using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

[Collection("Integration")]
public class DocumentTypeHandlerTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IMediator Mediator(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IMediator>();
    private IDataSetService DataSets(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDataSetService>();

    private static JsonDocument EmptySchema() => JsonDocument.Parse(@"{""fields"":[]}");

    private async Task<Guid> CreateSetAsync(IServiceScope scope)
    {
        var m = Mediator(scope);
        var c = await m.Send(new CreateConstructionCommand("Объект", Guid.NewGuid()));
        var s = await m.Send(new CreateSectionCommand(c.Id, "Раздел"));
        var set = await m.Send(new CreateDocumentSetCommand(s.Id, "Комплект"));
        return set.Id;
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_PersistsDocumentType()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("АОСР", "AOSR", DocumentTypeKind.Document, null, EmptySchema()));

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("АОСР", created.Name);
        Assert.Equal("AOSR", created.Code);
        Assert.Equal(DocumentTypeKind.Document, created.Kind);
        Assert.False(created.IsAbstract);
    }

    // ── GetById ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_ReturnsNull_WhenNotFound()
    {
        using var scope = fixture.Services.CreateScope();
        var result = await Mediator(scope).Send(new GetDocumentTypeQuery(Guid.NewGuid()));
        Assert.Null(result);
    }

    // ── UpdateSchema ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateSchema_ChangesSchemaInPlace()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Тип", "T1", DocumentTypeKind.Document, null, EmptySchema()));

        var newSchema = JsonDocument.Parse(@"{""fields"":[{""key"":""name"",""title"":""Имя"",""type"":""string""}]}");

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(
            new UpdateDocumentTypeSchemaCommand(created.Id, newSchema));

        Assert.Equal(created.Id, updated.Id);
        using var doc = updated.Schema;
        Assert.True(doc.RootElement.TryGetProperty("fields", out _));
    }

    // ── Rename / SetParent ────────────────────────────────────────────────────

    [Fact]
    public async Task Update_RenamesDocumentType()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Старое", "OLD1", DocumentTypeKind.Document, null, EmptySchema()));

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(
            new UpdateDocumentTypeCommand(created.Id, "Новое", "NEW1", null));

        Assert.Equal("Новое", updated.Name);
        Assert.Equal("NEW1", updated.Code);
        Assert.Null(updated.ParentId);
    }

    [Fact]
    public async Task Update_SetsParentId()
    {
        using var scope = fixture.Services.CreateScope();
        var parent = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Базовый", "BASE", DocumentTypeKind.Document, null, EmptySchema()));
        var child = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Дочерний", "CHILD", DocumentTypeKind.Document, null, EmptySchema()));

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(
            new UpdateDocumentTypeCommand(child.Id, "Дочерний", "CHILD", parent.Id));

        Assert.Equal(parent.Id, updated.ParentId);
    }

    // ── Cycle detection ───────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ThrowsOnCyclicParent()
    {
        using var scope = fixture.Services.CreateScope();
        var parent = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Родитель", "PAR", DocumentTypeKind.Document, null, EmptySchema()));
        var child = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Дочерний", "CHD", DocumentTypeKind.Document, parent.Id, EmptySchema()));

        using var scope2 = fixture.Services.CreateScope();
        // Trying to set child as parent of parent → cycle
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Mediator(scope2).Send(new UpdateDocumentTypeCommand(parent.Id, "Родитель", "PAR", child.Id)));
    }

    // ── SetAbstract ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAbstract_TogglesFlag()
    {
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Тип", "T2", DocumentTypeKind.Document, null, EmptySchema(), false));

        using var scope2 = fixture.Services.CreateScope();
        var updated = await Mediator(scope2).Send(
            new SetDocumentTypeAbstractCommand(created.Id, true));

        Assert.True(updated.IsAbstract);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_WithChildren_ThrowsInvalidOperation()
    {
        using var scope = fixture.Services.CreateScope();
        var parent = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Родитель", "P3", DocumentTypeKind.Document, null, EmptySchema()));
        await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Дочерний", "C3", DocumentTypeKind.Document, parent.Id, EmptySchema()));

        using var scope2 = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Mediator(scope2).Send(new DeleteDocumentTypeCommand(parent.Id)));
    }

    // issue #57: удаление типа не проверяло использование — ниже тесты на каждую из добавленных проверок.

    [Fact]
    public async Task Delete_Unused_Succeeds()
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Неиспользуемый", "UNUSED1", DocumentTypeKind.Document, null, EmptySchema()));

        using var scope2 = fixture.Services.CreateScope();
        await Mediator(scope2).Send(new DeleteDocumentTypeCommand(dt.Id));

        using var scope3 = fixture.Services.CreateScope();
        Assert.Null(await Mediator(scope3).Send(new GetDocumentTypeQuery(dt.Id)));
    }

    // issue #275: проактивный usage-запрос — тот же источник проверок, что и guard удаления.
    [Fact]
    public async Task Usage_Unused_IsEmpty()
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Свободный", "USAGE0", DocumentTypeKind.Document, null, EmptySchema()));

        using var scope2 = fixture.Services.CreateScope();
        var usage = await Mediator(scope2).Send(new GetDocumentTypeUsageQuery(dt.Id));
        Assert.False(usage.InUse);
        Assert.Empty(usage.Reasons);
    }

    [Fact]
    public async Task Usage_WithChildren_ReportsReason()
    {
        using var scope = fixture.Services.CreateScope();
        var parent = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Родитель", "USAGE_P", DocumentTypeKind.Document, null, EmptySchema()));
        await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Дочерний", "USAGE_C", DocumentTypeKind.Document, parent.Id, EmptySchema()));

        using var scope2 = fixture.Services.CreateScope();
        var usage = await Mediator(scope2).Send(new GetDocumentTypeUsageQuery(parent.Id));
        Assert.True(usage.InUse);
        var reason = Assert.Single(usage.Reasons, r => r.Kind == "children");
        Assert.Equal(1, reason.Count);
        Assert.Contains("Дочерний", reason.Names);
    }

    [Fact]
    public async Task Delete_WithDocumentInstance_ThrowsInvalidOperation()
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Тип с документом", "DI1", DocumentTypeKind.Document, null, EmptySchema()));
        var setId = await CreateSetAsync(scope);
        await Mediator(scope).Send(new AddDocumentToSetCommand(setId, dt.Id));

        using var scope2 = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Mediator(scope2).Send(new DeleteDocumentTypeCommand(dt.Id)));
    }

    [Fact]
    public async Task Delete_WithTemplate_ThrowsInvalidOperation()
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Тип с шаблоном", "TPL1", DocumentTypeKind.Document, null, EmptySchema()));
        await Mediator(scope).Send(new CreateTemplateCommand(dt.Id, "Шаблон", "= Заголовок"));

        using var scope2 = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Mediator(scope2).Send(new DeleteDocumentTypeCommand(dt.Id)));
    }

    [Fact]
    public async Task Delete_WithQualityDocument_ThrowsInvalidOperation()
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Тип документа качества", "QD1", DocumentTypeKind.Document, null, EmptySchema()));
        await Mediator(scope).Send(new CreateQualityDocumentCommand(
            dt.Id, "Сертификат", EmptySchema(), CatalogScope.System, null, QualityDocSource.Manual, null, null, null));

        using var scope2 = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Mediator(scope2).Send(new DeleteDocumentTypeCommand(dt.Id)));
    }

    [Fact]
    public async Task Delete_WithCommonDataEntry_ThrowsInvalidOperation()
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Составной тип", "CDE1", DocumentTypeKind.Composite, null, EmptySchema()));
        await Mediator(scope).Send(new CreateCommonDataEntryCommand(
            "Запись", dt.Id, JsonDocument.Parse("{}"), CatalogScope.System, null));

        using var scope2 = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Mediator(scope2).Send(new DeleteDocumentTypeCommand(dt.Id)));
    }

    [Fact]
    public async Task Delete_WithBindingTemplate_ThrowsInvalidOperation()
    {
        using var scope = fixture.Services.CreateScope();
        var dt = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Тип с шаблоном привязки", "BT1", DocumentTypeKind.Document, null, EmptySchema()));
        await DataSets(scope).CreateTemplateAsync(dt.Id, new CreateTemplateInput("Маппинг", null, null), default);

        using var scope2 = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Mediator(scope2).Send(new DeleteDocumentTypeCommand(dt.Id)));
    }

    [Fact]
    public async Task Delete_WithMaterializedSource_ThrowsInvalidOperation()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = DataSets(scope);
        var dt = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Тип материализации", "MAT1", DocumentTypeKind.Composite, null, EmptySchema()));

        var csv = System.Text.Encoding.UTF8.GetBytes("A,B\n1,2\n");
        var file = await svc.UploadFileAsync(new UploadFileInput(csv, "t.csv", "text/csv", "Тест", "System", null), default);
        var candidate = (await svc.DetectSourceCandidatesAsync(file.Id, default)).Single();
        var source = await svc.CreateSourceAsync(file.Id, new CreateSourceInput("Данные", candidate.SheetOrPath, null), default);
        await svc.SetMaterializationAsync(source.Id, dt.Id, new(), default);

        using var scope2 = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Mediator(scope2).Send(new DeleteDocumentTypeCommand(dt.Id)));
    }

    [Fact]
    public async Task Delete_ReferencedAsComplexFieldInOtherTypeSchema_ThrowsInvalidOperation()
    {
        using var scope = fixture.Services.CreateScope();
        var composite = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Организация", "ORG1", DocumentTypeKind.Composite, null, EmptySchema()));
        var userSchema = JsonDocument.Parse(
            $$"""{"fields":[{"key":"org","type":"complex","typeId":"{{composite.Id}}"}]}""");
        await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Договор", "CTR1", DocumentTypeKind.Document, null, userSchema));

        using var scope2 = fixture.Services.CreateScope();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Mediator(scope2).Send(new DeleteDocumentTypeCommand(composite.Id)));
        Assert.Contains("Договор", ex.Message);
    }

    [Fact]
    public async Task Delete_SelfReferentialSchema_DoesNotBlockOwnDeletion()
    {
        // Составной тип, чья СОБСТВЕННАЯ схема ссылается сам на себя (напр. дерево) — самоссылка не
        // должна учитываться при проверке "кто использует этот тип", иначе тип станет неудаляемым навсегда.
        using var scope = fixture.Services.CreateScope();
        var created = await Mediator(scope).Send(
            new CreateDocumentTypeCommand("Узел дерева", "TREE1", DocumentTypeKind.Composite, null, EmptySchema()));
        var selfRefSchema = JsonDocument.Parse(
            $$"""{"fields":[{"key":"children","type":"array","typeId":"{{created.Id}}"}]}""");
        using var scope2 = fixture.Services.CreateScope();
        await Mediator(scope2).Send(new UpdateDocumentTypeSchemaCommand(created.Id, selfRefSchema));

        using var scope3 = fixture.Services.CreateScope();
        await Mediator(scope3).Send(new DeleteDocumentTypeCommand(created.Id));

        using var scope4 = fixture.Services.CreateScope();
        Assert.Null(await Mediator(scope4).Send(new GetDocumentTypeQuery(created.Id)));
    }
}
