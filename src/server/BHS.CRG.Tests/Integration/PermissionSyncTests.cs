using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Права, объявленные кодом, доезжают до базы — и исчезнувшие из кода не теряются (issue #944,
/// ТЗ AUTH-1).
///
/// Вторая половина важнее первой. На право ссылается роль, и удаление строки оборвало бы ссылку:
/// администратор увидел бы роль, молча потерявшую часть состава, и узнал бы об этом от того, у
/// кого перестало работать. Поэтому исчезнувшее право остаётся в таблице со снятым признаком —
/// след, который видно.
/// </summary>
[Collection("Integration")]
public class PermissionSyncTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Declared_permissions_reach_the_database()
    {
        _ = fixture.CreateClient();

        var catalog = fixture.Services.GetRequiredService<PermissionCatalog>();
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stored = await db.Permissions.ToListAsync();

        foreach (var declared in catalog.All)
        {
            var row = stored.SingleOrDefault(p => p.Code == declared.Code);
            Assert.True(row is not null, $"Право «{declared.Code}» объявлено кодом, но в базу не попало.");
            Assert.True(row!.IsDeclared, $"Право «{declared.Code}» объявлено, но помечено как невыдаваемое.");
            Assert.Equal(declared.Gives, row.Gives);
            Assert.Equal(declared.Opens, row.Opens);
        }
    }

    /// <summary>Права ядра из ТЗ объявлены все: список в коде и есть тот справочник, по которому раздают доступ.</summary>
    [Fact]
    public void Core_permissions_are_declared()
    {
        _ = fixture.CreateClient();
        var codes = fixture.Services.GetRequiredService<PermissionCatalog>().Codes;

        foreach (var expected in CorePermissions.All.Select(p => p.Code))
            Assert.Contains(expected, codes);
    }

    /// <summary>
    /// Право, исчезнувшее из кода, остаётся в базе со снятым признаком — и возвращается, когда
    /// возвращается в код.
    /// </summary>
    [Fact]
    public async Task Vanished_permission_is_kept_and_marked()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var temporary = new AppPermission(
            "probe.thing.read", "видеть пробную вещь", "пробные данные", ["core.audit.read"]);

        // Появилось в коде.
        await PermissionSynchronizer.SyncAsync(db, new PermissionCatalog([temporary]), NullLogger.Instance);
        var added = await db.Permissions.SingleAsync(p => p.Code == temporary.Code);
        Assert.True(added.IsDeclared);
        Assert.Equal(["core.audit.read"], added.UsuallyWith);

        // Исчезло из кода: строка на месте, но право не выдаётся.
        await PermissionSynchronizer.SyncAsync(db, new PermissionCatalog([]), NullLogger.Instance);
        db.ChangeTracker.Clear();
        var vanished = await db.Permissions.SingleAsync(p => p.Code == temporary.Code);
        Assert.False(vanished.IsDeclared);

        // Вернулось — вместе с уточнённым объяснением.
        await PermissionSynchronizer.SyncAsync(
            db,
            new PermissionCatalog([temporary with { Gives = "видеть пробную вещь и её состав" }]),
            NullLogger.Instance);
        db.ChangeTracker.Clear();
        var restored = await db.Permissions.SingleAsync(p => p.Code == temporary.Code);
        Assert.True(restored.IsDeclared);
        Assert.Equal("видеть пробную вещь и её состав", restored.Gives);

        db.Permissions.Remove(restored);
        await db.SaveChangesAsync();
    }

    /// <summary>Повторная сверка ничего не задваивает: она идёт при каждом старте.</summary>
    [Fact]
    public async Task Sync_is_idempotent()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var catalog = fixture.Services.GetRequiredService<PermissionCatalog>();

        var before = await db.Permissions.CountAsync();
        await PermissionSynchronizer.SyncAsync(db, catalog, NullLogger.Instance);
        await PermissionSynchronizer.SyncAsync(db, catalog, NullLogger.Instance);

        Assert.Equal(before, await db.Permissions.CountAsync());
    }
}
