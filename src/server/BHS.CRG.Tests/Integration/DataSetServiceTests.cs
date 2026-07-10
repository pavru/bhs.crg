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

    // ── Files / Sources / Bindings (EF-tracking-чувствительные пути) ────────────

    private static readonly byte[] CsvBytes = System.Text.Encoding.UTF8.GetBytes("A,B\n1,2\n3,4\n");

    private async Task<DataSetFileDto> UploadCsvAsync(IServiceScope scope) =>
        await Svc(scope).UploadFileAsync(
            new UploadFileInput(CsvBytes, "test.csv", "text/csv", "Тест", "System", null), default);

    // Источники создаются ЯВНО (issue #20): загрузить CSV + создать источник «весь файл» из кандидата.
    private async Task<(DataSetFileDto File, DataSetSourceDto Source)> UploadCsvWithSourceAsync(IServiceScope scope)
    {
        var file = await UploadCsvAsync(scope);
        var candidate = (await Svc(scope).DetectSourceCandidatesAsync(file.Id, default)).Single();
        var source = await Svc(scope).CreateSourceAsync(
            file.Id, new CreateSourceInput("Данные", candidate.SheetOrPath, null), default);
        return (file, source);
    }

    [Fact]
    public async Task UploadCsv_NoAutoSources_CandidatesDetectColumns()
    {
        using var scope = fixture.Services.CreateScope();
        var file = await UploadCsvAsync(scope);

        // Набор = сырьё: источники НЕ авто-создаются при загрузке (issue #20).
        Assert.Empty(file.Sources);

        // Детект-кандидаты предлагают «весь файл» с колонками A/B — подсказка для явного создания.
        var candidates = await Svc(scope).DetectSourceCandidatesAsync(file.Id, default);
        Assert.Single(candidates);
        Assert.Contains(candidates[0].Columns, c => c.Name == "A");
        Assert.Contains(candidates[0].Columns, c => c.Name == "B");

        // Явное создание источника из кандидата кэширует те же колонки.
        var source = await Svc(scope).CreateSourceAsync(
            file.Id, new CreateSourceInput("Данные", candidates[0].SheetOrPath, null), default);
        Assert.Contains("\"A\"", source.CachedSchema);
        Assert.Contains("\"B\"", source.CachedSchema);
    }

    [Fact]
    public async Task DuplicateSource_PersistsAsNewSource()
    {
        // Прямая проверка хрупкого EF add-tracking (копия — новый дочерний источник на отслеживаемом файле).
        using var scope = fixture.Services.CreateScope();
        var (file, src) = await UploadCsvWithSourceAsync(scope);
        var srcId = src.Id;

        var copy = await Svc(scope).DuplicateSourceAsync(srcId, default);
        Assert.NotNull(copy);
        Assert.NotEqual(srcId, copy!.Id);

        using var scope2 = fixture.Services.CreateScope();
        var sources = await Svc(scope2).ListSourcesAsync(file.Id, default); // перечитываем свежим контекстом
        Assert.Equal(2, sources.Count);
    }

    [Fact]
    public async Task DeleteSource_BlockedWhenBindingExists()
    {
        var typeId = await CreateDocumentTypeAsync();
        Guid srcId;
        using (var scope = fixture.Services.CreateScope())
        {
            var (_, src) = await UploadCsvWithSourceAsync(scope);
            srcId = src.Id;
            var entry = await scope.ServiceProvider.GetRequiredService<MediatR.IMediator>().Send(
                new CreateCommonDataEntryCommand("Запись", typeId, JsonDocument.Parse("{}"),
                    BHS.CRG.Domain.Catalog.CatalogScope.System, null));
            await Svc(scope).CreateBindingAsync(
                new CreateBindingInput(null, entry.Id, srcId, null, new() { ["Поле"] = "A" }), default);
        }

        using var scope2 = fixture.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Svc(scope2).DeleteSourceAsync(srcId, default));
    }

    [Fact]
    public async Task Binding_CreateAndList_RoundTrips()
    {
        var typeId = await CreateDocumentTypeAsync();
        using var scope = fixture.Services.CreateScope();
        var (_, src) = await UploadCsvWithSourceAsync(scope);
        var entry = await scope.ServiceProvider.GetRequiredService<MediatR.IMediator>().Send(
            new CreateCommonDataEntryCommand("Запись", typeId, JsonDocument.Parse("{}"),
                BHS.CRG.Domain.Catalog.CatalogScope.System, null));

        var created = await Svc(scope).CreateBindingAsync(
            new CreateBindingInput(null, entry.Id, src.Id, null, new() { ["Поле"] = "A" }), default);
        Assert.NotNull(created);

        var list = await Svc(scope).ListBindingsAsync(null, entry.Id, default);
        Assert.Single(list);
        Assert.Equal(src.Id, list[0].SourceId);
    }

    [Fact]
    public async Task Binding_MaterializedSource_ExposesMaterializeMappingOnSource()
    {
        // issue #55: клиент вычисляет "эффективный маппинг" скалярной привязки (какие поля
        // реквизитов она реально покрывает) без похода на сервер — для этого DataSetBindingDto.Source
        // должен нести MaterializeMapping (не только MaterializeTypeId), см. DataSetDtoMapper.MapBinding.
        var typeId = await CreateDocumentTypeAsync();
        using var scope = fixture.Services.CreateScope();
        var svc = Svc(scope);
        var (_, src) = await UploadCsvWithSourceAsync(scope);
        await svc.SetMaterializationAsync(src.Id, typeId, new() { ["Поле"] = "A" }, default);

        var entry = await scope.ServiceProvider.GetRequiredService<MediatR.IMediator>().Send(
            new CreateCommonDataEntryCommand("Запись", typeId, JsonDocument.Parse("{}"),
                BHS.CRG.Domain.Catalog.CatalogScope.System, null));
        // Своя привязка без явного маппинга — эффективный маппинг берётся с материализации источника.
        await svc.CreateBindingAsync(new CreateBindingInput(null, entry.Id, src.Id, null, null), default);

        var list = await svc.ListBindingsAsync(null, entry.Id, default);
        var binding = Assert.Single(list);
        Assert.Empty(binding.Mapping);
        Assert.NotNull(binding.Source);
        Assert.Equal(typeId, binding.Source!.MaterializeTypeId);
        Assert.NotNull(binding.Source.MaterializeMapping);
        Assert.Equal("A", binding.Source.MaterializeMapping!["Поле"]);
    }
}
