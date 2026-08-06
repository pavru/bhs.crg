using System.Text;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Documents;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Наборы верхних уровней видны на нижних (issue #721).
///
/// Экран уровня показывал только свои наборы, а селектор источника у привязки — всю цепочку
/// «система → стройка → раздел → комплект». Расхождение никто не задумывал: два списка писались под
/// разные задачи. Теперь правило одно и живёт в одном месте, поэтому проверяется оно с ОБЕИХ сторон —
/// и через список уровня, и через список доступного документу.
///
/// Проверяется именно граница: не «список стал длиннее», а что чужая ветка в него не попадает —
/// иначе наборы соседней стройки оказались бы предложены к привязке.
/// </summary>
[Collection("Integration")]
public class InheritedDataSetFilesTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IMediator M(IServiceScope s) => s.ServiceProvider.GetRequiredService<IMediator>();
    private static IDataSetService Svc(IServiceScope s) => s.ServiceProvider.GetRequiredService<IDataSetService>();

    private sealed record Tree(Guid ConstructionId, Guid SectionId, Guid SetId, Guid OtherConstructionId, Guid OtherSetId);

    /// <summary>Две стройки: своя (с разделом и комплектом) и посторонняя — граница проверяется по ней.</summary>
    private static async Task<Tree> SeedAsync(IServiceScope scope)
    {
        var m = M(scope);
        var construction = await m.Send(new CreateConstructionCommand("ДНС-Е", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "250701.ЭОМ-1"));

        var other = await m.Send(new CreateConstructionCommand("Чужая стройка", Guid.NewGuid()));
        var otherSection = await m.Send(new CreateSectionCommand(other.Id, "ЭОМ"));
        var otherSet = await m.Send(new CreateDocumentSetCommand(otherSection.Id, "Чужой комплект"));

        return new Tree(construction.Id, section.Id, set.Id, other.Id, otherSet.Id);
    }

    private static Task<DataSetFileDto> FileAsync(IServiceScope scope, string name, string level, Guid? levelId) =>
        Svc(scope).UploadFileAsync(
            new UploadFileInput(Encoding.UTF8.GetBytes("A;B\n1;2\n"), $"{name}.csv", "text/csv", name, level, levelId?.ToString()),
            default);

    private static async Task<string[]> NamesAsync(IServiceScope scope, string level, Guid? levelId, bool inherited) =>
        [.. (await Svc(scope).ListFilesAsync(level, levelId, inherited, default)).Select(f => f.Name).Order()];

    [Fact]
    public async Task LevelSeesItsOwnFilesPlusEveryAncestor()
    {
        using var scope = fixture.Services.CreateScope();
        var t = await SeedAsync(scope);

        await FileAsync(scope, "Общий справочник", "System", null);
        await FileAsync(scope, "Кабели стройки", "Construction", t.ConstructionId);
        await FileAsync(scope, "Схемы раздела", "Section", t.SectionId);
        await FileAsync(scope, "Журнал комплекта", "Set", t.SetId);
        // Чужая ветка — её не должно быть видно нигде в нашей.
        await FileAsync(scope, "Кабели чужой стройки", "Construction", t.OtherConstructionId);
        await FileAsync(scope, "Журнал чужого комплекта", "Set", t.OtherSetId);

        Assert.Equal(
            ["Журнал комплекта", "Кабели стройки", "Общий справочник", "Схемы раздела"],
            await NamesAsync(scope, "Set", t.SetId, inherited: true));

        // Раздел не видит наборов своего же комплекта: наследование идёт сверху вниз, не наоборот.
        Assert.Equal(
            ["Кабели стройки", "Общий справочник", "Схемы раздела"],
            await NamesAsync(scope, "Section", t.SectionId, inherited: true));

        Assert.Equal(
            ["Кабели стройки", "Общий справочник"],
            await NamesAsync(scope, "Construction", t.ConstructionId, inherited: true));

        // Системный уровень — верхний, наследовать неоткуда.
        Assert.Equal(["Общий справочник"], await NamesAsync(scope, "System", null, inherited: true));
    }

    /// <summary>Прежнее поведение сохранено: без флага уровень отдаёт только своё.</summary>
    [Fact]
    public async Task WithoutTheFlag_OnlyOwnFilesAreReturned()
    {
        using var scope = fixture.Services.CreateScope();
        var t = await SeedAsync(scope);

        await FileAsync(scope, "Общий справочник", "System", null);
        await FileAsync(scope, "Кабели стройки", "Construction", t.ConstructionId);
        await FileAsync(scope, "Журнал комплекта", "Set", t.SetId);

        Assert.Equal(["Журнал комплекта"], await NamesAsync(scope, "Set", t.SetId, inherited: false));
        Assert.Empty(await NamesAsync(scope, "Section", t.SectionId, inherited: false));
    }

    /// <summary>
    /// Оба списка отвечают одинаково. Ради этого цепочка и вынесена в общий хелпер: пока правило
    /// было написано дважды, экран уровня и селектор привязки показывали разное — с этого issue и начался.
    /// </summary>
    [Fact]
    public async Task ScreenAndBindingPickerAgree()
    {
        using var scope = fixture.Services.CreateScope();
        var t = await SeedAsync(scope);

        await FileAsync(scope, "Общий справочник", "System", null);
        await FileAsync(scope, "Кабели стройки", "Construction", t.ConstructionId);
        await FileAsync(scope, "Схемы раздела", "Section", t.SectionId);
        await FileAsync(scope, "Журнал комплекта", "Set", t.SetId);
        await FileAsync(scope, "Журнал чужого комплекта", "Set", t.OtherSetId);

        var screen = await NamesAsync(scope, "Set", t.SetId, inherited: true);
        var picker = (await Svc(scope).ListAvailableFilesAsync(t.SetId, default)).Select(f => f.Name).Order().ToArray();

        Assert.Equal(screen, picker);
    }

    /// <summary>Уровень-владелец приезжает вместе с набором — иначе унаследованный от своего не отличить.</summary>
    [Fact]
    public async Task OwnerLevelTravelsWithTheFile()
    {
        using var scope = fixture.Services.CreateScope();
        var t = await SeedAsync(scope);

        await FileAsync(scope, "Кабели стройки", "Construction", t.ConstructionId);
        await FileAsync(scope, "Журнал комплекта", "Set", t.SetId);

        var files = await Svc(scope).ListFilesAsync("Set", t.SetId, true, default);

        var inherited = files.Single(f => f.Name == "Кабели стройки");
        Assert.Equal("Construction", inherited.Scope);
        Assert.Equal(t.ConstructionId, inherited.ScopeId);

        var own = files.Single(f => f.Name == "Журнал комплекта");
        Assert.Equal("Set", own.Scope);
        Assert.Equal(t.SetId, own.ScopeId);
    }
}
