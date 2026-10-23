using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Порты классификатора видов работ и справочника единиц (ТЗ CORE-20.4, PLAN-6.1): модуль получает вид
/// работы с единицей и признаками типизированно, а «справочник не заведён» отличает от «пусто».
///
/// <para>Сторож задачи — переименованные ключи: заказчик вправе переименовать поле единицы и признака
/// (уровень «расширяемый»), и порт, читающий по ключу, после этого заводил бы позицию перечня без
/// единицы — молча.</para>
/// </summary>
[Collection("Integration")]
public class ModuleWorkTypesPortTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly WorkTypeSeed seed = new(fixture);

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    private readonly List<IServiceScope> scopes = [];

    public Task DisposeAsync()
    {
        foreach (var scope in scopes) scope.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Единица_и_признак_читаются_при_переименованных_ключах()
    {
        var unit = await seed.TypeAsync(CoreRecordTypes.UnitCode, "Единица измерения");
        // Классификатор, в котором заказчик переименовал поля: ключей объявления ядра здесь нет вовсе.
        var type = await seed.TypeAsync(CoreRecordTypes.WorkTypeCode, "Вид работы", $$"""
            {"fields":[
              {"key":"Шифр","title":"Шифр","type":"string","tags":["identity:1"]},
              {"key":"Работа","title":"Работа","type":"string","tags":["identity:2"]},
              {"key":"Ед","title":"Ед.","type":"complex","typeId":"{{unit.Id}}"},
              {"key":"НужноМесто","title":"Место","type":"boolean","tags":["{{FunctionalTag.WorkRequiresLocation}}"]},
              {"key":"ВИД","title":"В ИД","type":"boolean","tags":["{{FunctionalTag.WorkProducesId}}"]}
            ]}
            """);
        var metre = await seed.RecordAsync(unit.Id, "м");
        var work = await seed.RecordAsync(type.Id, "Прокладка кабеля", JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Шифр"] = "01.02", ["Работа"] = "Прокладка кабеля", ["Ед"] = WorkTypeSeed.Ref(metre),
            ["НужноМесто"] = true, ["ВИД"] = false,
        }));

        var got = Assert.Single((await Port<IModuleWorkTypes>().GetAsync([work.Id]))!);

        Assert.Equal("01.02", got.Code);
        Assert.Equal("Прокладка кабеля", got.Name);
        Assert.Equal(metre.Id, got.UnitId);
        Assert.Equal("м", got.UnitName);
        Assert.True(got.RequiresLocation);
        Assert.False(got.RequiresMaterials);
        Assert.False(got.ProducesId);
    }

    /// <summary>
    /// Справочника единиц нет — классификатора нет тоже (<c>SkipWhenBlocked</c>), и порт говорит «не
    /// заведён», а не отдаёт пустой выбор. Заведены, но пусты, — это уже пустой список, а не «нет».
    /// </summary>
    [Fact]
    public async Task Без_справочника_единиц_не_заведён_а_не_пусто()
    {
        Assert.Null(await seed.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.WorkType)));

        Assert.Null(await Port<IModuleWorkTypes>().SearchAsync(null, 10));
        Assert.Null(await Port<IModuleWorkTypes>().GetAsync([Guid.NewGuid()]));
        Assert.Null(await Port<IModuleUnits>().SearchAsync(null, 10));

        await seed.ClassifierAsync();
        var empty = await Port<IModuleWorkTypes>().SearchAsync(null, 10);
        Assert.NotNull(empty);
        Assert.Empty(empty.Items);
    }

    /// <summary>Поиск по части кода или наименования отдаёт живые виды с единицами, архивные — числом.</summary>
    [Fact]
    public async Task Поиск_вида_работы_отдаёт_живые_с_единицей_а_архивные_числом()
    {
        var (unit, type) = await seed.ClassifierAsync();
        var metre = await seed.RecordAsync(unit.Id, "м");
        var cable = await seed.RecordAsync(type.Id, "Прокладка кабеля", WorkTypeSeed.WorkTypeData("01.02", "Прокладка кабеля", metre, requiresLocation: true));
        var old = await seed.RecordAsync(type.Id, "Прокладка провода", WorkTypeSeed.WorkTypeData("01.03", "Прокладка провода", metre));
        await seed.RecordAsync(type.Id, "Монтаж щита", WorkTypeSeed.WorkTypeData("02.01", "Монтаж щита", metre));
        await seed.ArchiveAsync(old.Id);

        var byCode = (await Port<IModuleWorkTypes>().SearchAsync("01.0", 10))!;
        var hit = Assert.Single(byCode.Items);
        Assert.Equal(cable.Id, hit.Id);
        Assert.Equal("м", hit.UnitName);
        Assert.True(hit.RequiresLocation);
        Assert.Equal(1, byCode.InArchive);

        var byName = (await Port<IModuleWorkTypes>().SearchAsync("щит", 10))!;
        Assert.Equal("02.01", Assert.Single(byName.Items).Code);

        // Показ уже стоящего — архивный вид на месте, с признаком.
        Assert.True(Assert.Single((await Port<IModuleWorkTypes>().GetAsync([old.Id]))!).Archived);
    }

    /// <summary>Единицы: поиск на выбор без архивных (они числом), по идентификаторам — архивная помечена.</summary>
    [Fact]
    public async Task Порт_единиц_ищет_живые_а_архивную_помечает()
    {
        var unit = await seed.TypeAsync(CoreRecordTypes.UnitCode, "Единица измерения");
        var metre = await seed.RecordAsync(unit.Id, "м");
        var squared = await seed.RecordAsync(unit.Id, "м2");
        await seed.RecordAsync(unit.Id, "шт");
        await seed.ArchiveAsync(squared.Id);

        var found = (await Port<IModuleUnits>().SearchAsync("м", 10))!;
        Assert.Equal([metre.Id], found.Items.Select(u => u.Id));
        Assert.Equal(1, found.InArchive);

        var shown = (await Port<IModuleUnits>().GetAsync([metre.Id, squared.Id]))!;
        Assert.True(shown.Single(u => u.Id == squared.Id).Archived);
        Assert.False(shown.Single(u => u.Id == metre.Id).Archived);
    }

    /// <summary>Порт из своей области — как его получает модуль на запросе.</summary>
    private TPort Port<TPort>() where TPort : notnull
    {
        var scope = fixture.Services.CreateScope();
        scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<TPort>();
    }
}
