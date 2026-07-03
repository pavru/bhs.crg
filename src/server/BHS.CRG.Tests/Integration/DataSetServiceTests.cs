using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Покрывает <see cref="IDataSetService"/> через реальный DbContext — в частности
/// DataSetProcessingTemplateService/DataSetBindingTemplateService, выделенные из
/// DataSetService (см. архитектурный отчёт, «Предложение 3: декомпозиция DataSetService»).
/// До этого у DataSetService не было ни одного sквозного Integration-теста.
/// </summary>
[Collection("Integration")]
public class DataSetServiceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IDataSetService Svc(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDataSetService>();

    private async Task<Guid> CreateDocumentTypeAsync()
    {
        var code = $"DS_TEST_{Guid.NewGuid():N}";
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<MediatR.IMediator>();
        var dt = await m.Send(new CreateDocumentTypeCommand(
            code, code, DocumentTypeKind.Composite, null, JsonDocument.Parse(@"{""fields"":[]}")));
        return dt.Id;
    }

    // ── Processing templates ──────────────────────────────────────────────────

    [Fact]
    public async Task ProcessingTemplate_CreateListUpdateDelete_RoundTrips()
    {
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);

        var created = await svc.CreateProcessingTemplateAsync(
            new CreateProcessingTemplateInput("Шаблон А", "//row", null, null, null, null), default);
        Assert.Equal("Шаблон А", created.Name);

        var list = await svc.ListProcessingTemplatesAsync(default);
        Assert.Contains(list, t => t.Id == created.Id);

        var updated = await svc.UpdateProcessingTemplateAsync(created.Id,
            new UpdateProcessingTemplateInput("Шаблон Б", "//row2", null, null, null, null), default);
        Assert.NotNull(updated);
        Assert.Equal("Шаблон Б", updated!.Name);

        var deleted = await svc.DeleteProcessingTemplateAsync(created.Id, default);
        Assert.True(deleted);
        Assert.DoesNotContain(await svc.ListProcessingTemplatesAsync(default), t => t.Id == created.Id);
    }

    [Fact]
    public async Task ProcessingTemplate_UpdateUnknownId_ReturnsNull()
    {
        using var scope = fixture.Services.CreateScope();
        var result = await Svc(scope).UpdateProcessingTemplateAsync(Guid.NewGuid(),
            new UpdateProcessingTemplateInput("X", null, null, null, null, null), default);
        Assert.Null(result);
    }

    // ── Binding templates ─────────────────────────────────────────────────────

    [Fact]
    public async Task BindingTemplate_CreateListUpdateDelete_RoundTrips()
    {
        var docTypeId = await CreateDocumentTypeAsync();
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);

        var created = await svc.CreateTemplateAsync(docTypeId,
            new CreateTemplateInput("Маппинг 1", "поле1", new Dictionary<string, string> { ["поле1"] = "Колонка1" }), default);
        Assert.Equal("Маппинг 1", created.Name);
        Assert.Equal(0, created.SortOrder);

        var second = await svc.CreateTemplateAsync(docTypeId,
            new CreateTemplateInput("Маппинг 2", null, null), default);
        Assert.Equal(1, second.SortOrder); // maxOrder+1, не 0 — подтверждает, что SortOrder считается по тому же docTypeId

        var list = await svc.ListTemplatesAsync(docTypeId, default);
        Assert.Equal(2, list.Count);

        var updated = await svc.UpdateTemplateAsync(docTypeId, created.Id,
            new UpdateTemplateInput("Маппинг 1 (изм.)", "поле2", null, 5), default);
        Assert.NotNull(updated);
        Assert.Equal("Маппинг 1 (изм.)", updated!.Name);
        Assert.Equal(5, updated.SortOrder);

        var deleted = await svc.DeleteTemplateAsync(docTypeId, created.Id, default);
        Assert.True(deleted);
        Assert.Single(await svc.ListTemplatesAsync(docTypeId, default));
    }

    [Fact]
    public async Task BindingTemplate_WrongDocTypeId_IsNotFound()
    {
        var docTypeId = await CreateDocumentTypeAsync();
        var otherDocTypeId = await CreateDocumentTypeAsync();
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);

        var created = await svc.CreateTemplateAsync(docTypeId, new CreateTemplateInput("М", null, null), default);

        // Тот же id шаблона, но чужой docTypeId — обе операции должны трактовать как "не найдено".
        var updated = await svc.UpdateTemplateAsync(otherDocTypeId, created.Id,
            new UpdateTemplateInput("Другое", null, null, null), default);
        Assert.Null(updated);

        var deleted = await svc.DeleteTemplateAsync(otherDocTypeId, created.Id, default);
        Assert.False(deleted);
    }
}
