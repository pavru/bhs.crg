using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Tables;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Представления таблиц в резервной копии (ТЗ CORE-33, issue #1095): едут общие, личные — нет.
/// </summary>
public partial class BackupServiceTests
{
    private const string ViewsTable = "costs.invoices";
    private const string ViewState = """{"columns":["Номер","Дата"],"filter":null,"sort":[{"column":"Дата","descending":true}],"totals":[],"pinned":1}""";

    /// <summary>
    /// Общее представление переживает перенос под своим идентификатором — им оно названо в закладках —
    /// и приходит без автора: учётные записи копия не переносит. Личное не приходит вовсе.
    /// </summary>
    [Fact]
    public async Task Общее_представление_таблицы_едет_в_копии_а_личное_нет()
    {
        Guid sharedId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var author = await ViewAuthorAsync(scope);
            var shared = SavedTableView.Create(ViewsTable, "registry", author, "Счета к оплате", true, ViewState);
            sharedId = shared.Id;
            db.SavedTableViews.AddRange(shared, SavedTableView.Create(ViewsTable, null, author, "Моё", false, ViewState));
            await db.SaveChangesAsync();
        }

        var zip = await ExportedAsync();
        await fixture.ResetDatabaseAsync();

        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await Backup(scope).ImportAsync(new MemoryStream(zip));

        Assert.True(report.Success);
        using (var scope = fixture.Services.CreateScope())
        {
            var view = Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .SavedTableViews.AsNoTracking().ToListAsync());
            Assert.Equal(sharedId, view.Id);
            Assert.True(view.Shared);
            Assert.Null(view.OwnerId);
            Assert.Equal(("Счета к оплате", ViewsTable, "registry"), (view.Title, view.TableAddress, view.BasedOn));
            Assert.Equal(["Номер", "Дата"], BHS.CRG.Infrastructure.Tables.SavedTableViewRules.Read(view.State).Columns);
        }
    }

    /// <summary>
    /// Копия поверх живой системы чужих решений не отменяет: представление, которое автор здесь вернул
    /// в свои, общим не становится, а занятое имя не затирается — и оба пропуска названы.
    /// </summary>
    [Fact]
    public async Task Копия_не_отменяет_возврат_в_свои_и_не_занимает_чужое_имя()
    {
        Guid returnedId;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var returned = SavedTableView.Create(ViewsTable, null, await ViewAuthorAsync(scope), "Вернут в свои", true, ViewState);
            returnedId = returned.Id;
            db.SavedTableViews.AddRange(returned, SavedTableView.Create(ViewsTable, null, null, "Занятое имя", true, ViewState));
            await db.SaveChangesAsync();
        }

        var zip = await ExportedAsync();

        // После снятия копии: первое автор вернул в свои, второе удалили и завели заново — тем же именем.
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.SavedTableViews.SingleAsync(v => v.Id == returnedId)).Change("Вернут в свои", false, ViewState);
            db.SavedTableViews.Remove(await db.SavedTableViews.SingleAsync(v => v.Title == "Занятое имя"));
            db.SavedTableViews.Add(SavedTableView.Create(ViewsTable, null, null, "занятое ИМЯ", true, "{}"));
            await db.SaveChangesAsync();
        }

        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await Backup(scope).ImportAsync(new MemoryStream(zip));

        Assert.True(report.Success);
        Assert.Contains(report.Warnings, w => w.Contains("вернул их в свои"));
        Assert.Contains(report.Warnings, w => w.Contains("с таким названием"));
        using (var scope = fixture.Services.CreateScope())
        {
            var views = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SavedTableViews.AsNoTracking().ToListAsync();
            Assert.Equal(2, views.Count);
            Assert.False(views.Single(v => v.Id == returnedId).Shared);
            Assert.Equal("занятое ИМЯ", views.Single(v => v.Id != returnedId).Title);
        }
    }

    /// <summary>
    /// Цепочка переименований: здесь первое — «А», второе — «Б»; в копии первое — «Б», второе — «В».
    /// Имя «Б» к концу восстановления свободно, и первое обязано его получить — а не остаться прежним
    /// с ложным «уже есть». И уникальный индекс посреди сохранения её не роняет.
    /// </summary>
    [Fact]
    public async Task Копия_переименовывает_цепочкой_и_повтор_ничего_не_трогает()
    {
        Guid first, second;
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var (one, two) = (SavedTableView.Create(ViewsTable, null, null, "Б", true, ViewState),
                SavedTableView.Create(ViewsTable, null, null, "В", true, ViewState));
            (first, second) = (one.Id, two.Id);
            db.SavedTableViews.AddRange(one, two);
            await db.SaveChangesAsync();
        }

        var zip = await ExportedAsync();

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // По одному имени за сохранение: порядок строк внутри сохранения не наш, а индекс
            // проверяется построчно — ровно то, что обходит восстановление своими двумя шагами.
            (await db.SavedTableViews.SingleAsync(v => v.Id == first)).Change("А", true, ViewState);
            await db.SaveChangesAsync();
            (await db.SavedTableViews.SingleAsync(v => v.Id == second)).Change("Б", true, ViewState);
            await db.SaveChangesAsync();
        }

        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await Backup(scope).ImportAsync(new MemoryStream(zip));

        Assert.True(report.Success);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("представлений таблиц"));
        Dictionary<Guid, uint> versions;
        using (var scope = fixture.Services.CreateScope())
        {
            var views = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SavedTableViews.AsNoTracking().ToListAsync();
            Assert.Equal("Б", views.Single(v => v.Id == first).Title);
            Assert.Equal("В", views.Single(v => v.Id == second).Title);
            versions = views.ToDictionary(v => v.Id, v => v.RowVersion);
        }

        // Та же копия второй раз: менять нечего — и версии строк стоят на месте. Сдвинься они, открытые
        // формы получали бы «тем временем изменили» без единого изменения.
        using (var scope = fixture.Services.CreateScope())
            Assert.True((await Backup(scope).ImportAsync(new MemoryStream(zip))).Success);
        using (var scope = fixture.Services.CreateScope())
        {
            var views = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SavedTableViews.AsNoTracking().ToListAsync();
            Assert.Equal(versions, views.ToDictionary(v => v.Id, v => v.RowVersion));
        }
    }

    private async Task<byte[]> ExportedAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var (zip, _) = await Backup(scope).ExportAsync();
        await using var handle = zip;
        using var ms = new MemoryStream();
        await zip.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static async Task<Guid> ViewAuthorAsync(IServiceScope scope)
    {
        var email = $"views_{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        Assert.True((await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
            .CreateAsync(user, "Passw0rd!")).Succeeded);
        return user.Id;
    }
}
