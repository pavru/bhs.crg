using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Спуск «уровень → комплекты поддерева» (issue #625) — единая реализация вместо трёх копий.
/// Проверяется через контракт слоя приложения: именно им пользуется список доступных документов,
/// и именно он раньше обходил дерево своими руками.
/// </summary>
[Collection("Integration")]
public class ScopeSubtreeTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private readonly Guid _userId = Guid.NewGuid();

    /// <summary>Две стройки: в первой два раздела (2 и 1 комплект), во второй — один комплект.</summary>
    private async Task<(Guid ConstructionA, Guid SectionA1, Guid[] SetsA1, Guid SetA2, Guid SetB)> SeedAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();

        var a = await m.Send(new CreateConstructionCommand("Стройка A", _userId));
        var a1 = await m.Send(new CreateSectionCommand(a.Id, "ЭОМ"));
        var a2 = await m.Send(new CreateSectionCommand(a.Id, "ОВиК"));
        var b = await m.Send(new CreateConstructionCommand("Стройка B", _userId));
        var b1 = await m.Send(new CreateSectionCommand(b.Id, "СС"));

        var s1 = await m.Send(new CreateDocumentSetCommand(a1.Id, "ЭОМ-1"));
        var s2 = await m.Send(new CreateDocumentSetCommand(a1.Id, "ЭОМ-2"));
        var s3 = await m.Send(new CreateDocumentSetCommand(a2.Id, "ОВиК-1"));
        var s4 = await m.Send(new CreateDocumentSetCommand(b1.Id, "СС-1"));

        return (a.Id, a1.Id, [s1.Id, s2.Id], s3.Id, s4.Id);
    }

    private async Task<IReadOnlyList<Guid>> UnderAsync(CatalogScope scope, Guid? scopeId)
    {
        using var s = fixture.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<IScopeSubtree>().SetIdsUnderAsync(scope, scopeId);
    }

    [Fact]
    public async Task Set_ReturnsItself()
    {
        var (_, _, setsA1, _, _) = await SeedAsync();
        Assert.Equal([setsA1[0]], await UnderAsync(CatalogScope.Set, setsA1[0]));
    }

    [Fact]
    public async Task Section_ReturnsItsSetsOnly()
    {
        var (_, sectionA1, setsA1, setA2, setB) = await SeedAsync();
        var under = await UnderAsync(CatalogScope.Section, sectionA1);
        Assert.Equal([.. setsA1.Order()], [.. under.Order()]);
        Assert.DoesNotContain(setA2, under); // соседний раздел той же стройки
        Assert.DoesNotContain(setB, under);
    }

    [Fact]
    public async Task Construction_ReturnsSetsOfAllItsSections()
    {
        var (constructionA, _, setsA1, setA2, setB) = await SeedAsync();
        var under = await UnderAsync(CatalogScope.Construction, constructionA);
        Assert.Equal([.. new[] { setsA1[0], setsA1[1], setA2 }.Order()], [.. under.Order()]);
        Assert.DoesNotContain(setB, under); // чужая стройка
    }

    /// <summary>«Все комплекты базы» — не поддерево: спросивший про System получает пусто, а не всё.</summary>
    [Fact]
    public async Task System_IsEmpty()
    {
        await SeedAsync();
        Assert.Empty(await UnderAsync(CatalogScope.System, null));
    }

    [Fact]
    public async Task LevelWithoutId_IsEmpty()
    {
        await SeedAsync();
        Assert.Empty(await UnderAsync(CatalogScope.Section, null));
        Assert.Empty(await UnderAsync(CatalogScope.Set, null));
    }

    [Fact]
    public async Task UnknownId_IsEmpty()
    {
        await SeedAsync();
        Assert.Empty(await UnderAsync(CatalogScope.Construction, Guid.NewGuid()));
    }

    /// <summary>Пустой раздел — пустое поддерево, а не отказ: раздел без комплектов законен.</summary>
    [Fact]
    public async Task EmptySection_IsEmpty()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var c = await m.Send(new CreateConstructionCommand("Пустая", _userId));
        var s = await m.Send(new CreateSectionCommand(c.Id, "Без комплектов"));

        Assert.Empty(await UnderAsync(CatalogScope.Section, s.Id));
        Assert.Empty(await UnderAsync(CatalogScope.Construction, c.Id));
    }
}
