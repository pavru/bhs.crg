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
