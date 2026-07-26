using System.Text.Json;
using BHS.CRG.Api.Mcp;
using BHS.CRG.Application.DataSnapshots;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Витрина прикрепления (issue #427). Ресурсы были объявлены только шаблонами URI и уезжали в
/// resources/templates/list, поэтому клиент, показывающий лишь resources/list, не видел ничего —
/// возможность «прикрепить объект к диалогу» не существовала ни в одном таком клиенте.
/// </summary>
[Collection("Integration")]
public class McpResourceCatalogTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<IReadOnlyList<Resource>> ListAsync(IServiceScope scope)
        => await McpResourceCatalog.BuildAsync(
            scope.ServiceProvider.GetRequiredService<IDomainSnapshotService>(),
            scope.ServiceProvider.GetRequiredService<IDataSnapshotService>());

    [Fact]
    public async Task Lists_Constructions_Sets_AndDatasets_ByHumanNames()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();

        var type = await m.Send(new CreateDocumentTypeCommand(
            "Акт", $"ACT_{Guid.NewGuid():N}"[..12], DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}""")));
        var construction = await m.Send(new CreateConstructionCommand("ДНС Сити", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "250701.ЭОМ-1"));
        await m.Send(new AddDocumentToSetCommand(set.Id, type.Id));

        var result = await ListAsync(scope);

        var c = Assert.Single(result, r => r.Uri == $"bhs://construction/{construction.Id}");
        Assert.Equal("ДНС Сити", c.Name);

        var s = Assert.Single(result, r => r.Uri == $"bhs://document-set/{set.Id}");
        Assert.Equal("250701.ЭОМ-1", s.Name);
        // Контекст обязателен: одноимённые комплекты разных разделов иначе неразличимы в списке.
        Assert.Contains("ДНС Сити", s.Description);
        Assert.Contains("ЭОМ", s.Description);
        Assert.Equal("application/json", s.MimeType);
    }

    /// <summary>
    /// Витрина — рабочие единицы, а не полный обход домена: документов сотни, типы это конфигурация,
    /// а записи каталога — то, на что ссылаются, а не то, что прикрепляют к разговору.
    /// </summary>
    [Fact]
    public async Task DoesNotList_Documents_Types_OrCatalogEntries()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();

        var type = await m.Send(new CreateDocumentTypeCommand(
            "Акт", $"ACT_{Guid.NewGuid():N}"[..12], DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}""")));
        var construction = await m.Send(new CreateConstructionCommand("ДНС Сити", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "ЭОМ-1"));
        var doc = await m.Send(new AddDocumentToSetCommand(set.Id, type.Id));

        var uris = (await ListAsync(scope)).Select(r => r.Uri).ToList();

        Assert.DoesNotContain($"bhs://document/{doc.Id}", uris);
        Assert.DoesNotContain($"bhs://document-type/{type.Id}", uris);
        Assert.DoesNotContain(uris, u => u.StartsWith("bhs://catalog-entry/"));
    }

    [Fact]
    public async Task EmptySystem_ListsNothing_NotThrows()
    {
        using var scope = fixture.Services.CreateScope();
        Assert.Empty(await ListAsync(scope));
    }
}
