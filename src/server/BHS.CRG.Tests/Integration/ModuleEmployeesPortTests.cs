using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Identity;
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

    /// <summary>
    /// Связь с учётной записью (ТЗ CORE-7, CORE-20.4; work-11-assignments) — из таблицы связи work-01, а не из
    /// поля карточки: связанный — «связан» с именем; удалённая учётная запись — «удалена», а не «не связан»;
    /// значение в запертом поле «УчётнаяЗапись» без строки связи — «не связан». Обратный вопрос отдаёт
    /// сотрудника действующей связи и <c>null</c> для учётной записи без неё.
    /// </summary>
    [Fact]
    public async Task Связь_с_учётной_записью_из_таблицы_связи_а_не_из_поля_карточки()
    {
        await seed.TypeAsync(CoreRecordTypes.PersonCode, "Лицо");
        var type = (await seed.SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.Employee)))!;
        var linked = await seed.RecordAsync(type.Id, "Сидоров Олег Павлович");
        var deleted = await seed.RecordAsync(type.Id, "Кузнецова Мария Игоревна");
        var field = await seed.RecordAsync(type.Id, "Волков Илья Андреевич");
        var sidorov = await AccountAsync("Сидоров О. П.");
        var kuznetsova = await AccountAsync("Кузнецова М. И.");
        var stranger = await AccountAsync("Без сотрудника");

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.EmployeeAccounts.Add(EmployeeAccount.Link(linked.Id, sidorov, "Сидоров О. П."));
            db.EmployeeAccounts.Add(EmployeeAccount.Link(deleted.Id, kuznetsova, "Кузнецова М. И."));
            await db.SaveChangesAsync();

            // Значение в поле карточки — мимо охраны: обычным сохранением его не поставить.
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DomainObject>>();
            var stored = (await repo.GetByIdAsync(field.Id))!;
            stored.SetData(JsonDocument.Parse($$"""{"УчётнаяЗапись":"{{stranger}}"}"""));
            repo.Update(stored);
            await repo.SaveChangesAsync();

            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.DeleteAsync((await users.FindByIdAsync(kuznetsova.ToString()))!)).Succeeded);
        }

        var shown = (await Port().GetAsync([linked.Id, deleted.Id, field.Id]))!.ToDictionary(e => e.Id, e => e.Account);
        Assert.Equal(new ModuleEmployeeAccount(ModuleAccountState.Linked, sidorov, "Сидоров О. П."), shown[linked.Id]);
        Assert.Equal(new ModuleEmployeeAccount(ModuleAccountState.Deleted, null, "Кузнецова М. И."), shown[deleted.Id]);
        Assert.Equal(ModuleEmployeeAccount.None, shown[field.Id]);

        Assert.Equal(linked.Id, (await Port().OfAccountAsync(sidorov))?.Id);
        Assert.Null(await Port().OfAccountAsync(stranger));
        Assert.Null(await Port().OfAccountAsync(kuznetsova));
    }

    /// <summary>Вымышленная учётная запись с уникальным адресом: учётные записи между прогонами не чистятся.</summary>
    private async Task<Guid> AccountAsync(string displayName)
    {
        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"empport_{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, "Passw0rd!")).Succeeded);
        return user.Id;
    }

    private IModuleEmployees Port()
    {
        var scope = fixture.Services.CreateScope();
        scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<IModuleEmployees>();
    }
}
