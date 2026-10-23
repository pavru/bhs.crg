using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Порт сотрудников (ТЗ PLAN-16, CORE-7): исполнители работ графика. Табельный номер и период работы —
/// по тэгам <c>employee.*</c>, а не по ключам; уволенный на дату помечается, а не пропадает; «справочник
/// не заведён» отличается от «сотрудников нет».
/// </summary>
[Collection("Integration")]
public class ModuleEmployeesPortTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private readonly WorkTypeSeed seed = new(fixture);
    private readonly List<IServiceScope> scopes = [];

    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();

    public Task DisposeAsync()
    {
        foreach (var scope in scopes) scope.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Табельный_и_период_по_тэгам_уволенный_помечен_а_не_пропал()
    {
        var person = await seed.TypeAsync(CoreRecordTypes.PersonCode, "Лицо");
        // Справочник, где заказчик переименовал поля: ключей объявления ядра нет, тэги на месте.
        var type = await seed.TypeAsync(CoreRecordTypes.EmployeeCode, "Сотрудник", $$"""
            {"fields":[
              {"key":"Таб","title":"Таб. №","type":"string","tags":["{{FunctionalTag.EmployeePersonnelNumber}}"]},
              {"key":"С","title":"С","type":"date","tags":["{{FunctionalTag.EmployeeEmployedFrom}}"]},
              {"key":"По","title":"По","type":"date","tags":["{{FunctionalTag.EmployeeEmployedTo}}"]}
            ]}
            """, person.Id);
        var ivanov = await seed.RecordAsync(type.Id, "Иванов Иван Иванович", """{"Таб":"0421","С":"2025-03-01"}""");
        var petrov = await seed.RecordAsync(type.Id, "Петров Пётр Петрович", """{"Таб":"0422","С":"2024-01-10","По":"2026-06-01"}""");
        var gone = await seed.RecordAsync(type.Id, "Петрова Анна", """{"Таб":"0423"}""");
        await seed.ArchiveAsync(gone.Id);

        var shown = (await Port().GetAsync([ivanov.Id, petrov.Id, gone.Id]))!;
        var iv = shown.Single(e => e.Id == ivanov.Id);
        Assert.Equal("Иванов Иван Иванович", iv.FullName);
        Assert.Equal("0421", iv.PersonnelNumber);
        Assert.Equal(new DateOnly(2025, 3, 1), iv.EmployedFrom);
        Assert.Null(iv.EmployedTo);
        Assert.True(shown.Single(e => e.Id == gone.Id).Archived);

        // На дату позже увольнения Петров в выдаче — после работающих и с пометкой.
        var onDate = new DateOnly(2026, 10, 23);
        var found = (await Port().SearchAsync("Петр", onDate, 10))!;
        var hit = Assert.Single(found.Items);
        Assert.Equal(petrov.Id, hit.Id);
        Assert.Equal("уволен с 01.06.2026", hit.NotWorkingOn(onDate));
        Assert.Equal(1, found.InArchive);

        var all = (await Port().SearchAsync(null, onDate, 10))!;
        Assert.Equal([ivanov.Id, petrov.Id], all.Items.Select(e => e.Id));
        Assert.Null(all.Items[0].NotWorkingOn(onDate));

        // Поиск по табельному номеру.
        Assert.Equal(ivanov.Id, Assert.Single((await Port().SearchAsync("0421", onDate, 10))!.Items).Id);
    }

    /// <summary>Нет «Персоны» — нет и справочника: порт говорит «не заведён», а не отдаёт пустой выбор.</summary>
    [Fact]
    public async Task Справочник_сотрудников_не_заведён_а_не_пуст()
    {
        Assert.Null(await seed.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.Employee)));

        Assert.Null(await Port().SearchAsync(null, new DateOnly(2026, 10, 23), 10));
        Assert.Null(await Port().GetAsync([Guid.NewGuid()]));
        Assert.Contains("«Сотрудник» не заведён", IModuleEmployees.NotSetUp);
    }

    private IModuleEmployees Port()
    {
        var scope = fixture.Services.CreateScope();
        scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IModuleEmployees>();
    }
}
