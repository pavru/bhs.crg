using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Проверки прав модуля на хосте «только этот модуль» — одним местом для <see cref="PlanOnlyHostTests" />
/// и <see cref="WorkOnlyHostTests" /> (ревью PR #1932). Копии различались только префиксом и списками, и
/// правка правила (храповика «без двери», точной сверки состава роли) иначе шла бы в каждую — а следующий
/// модуль добавил бы третью. Что ожидается и ПОЧЕМУ — по-прежнему у вызывающего класса, рядом с решением.
/// </summary>
internal static class ModuleOnlyChecks
{
    /// <summary>
    /// Права модуля объявлены ровно <paramref name="expected" />, каждое годно (AUTH-1) и доезжает до базы
    /// объявленным; прав выключенных модулей <paramref name="absent" /> в каталоге нет (AUTH-19).
    /// </summary>
    public static async Task<IReadOnlyList<AppPermission>> DeclaredAndStoredAsync(
        ModuleOnlyHost host, string module, string[] expected, params string[] absent)
    {
        _ = host.CreateClient();
        var prefix = module + ".";

        var catalog = host.Services.GetRequiredService<PermissionCatalog>();
        var declared = catalog.All.Where(p => p.Code.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        Assert.Equal(expected.Order(StringComparer.Ordinal), declared.Select(p => p.Code).Order(StringComparer.Ordinal));
        Assert.All(declared, p => Assert.Null(p.Validate()));
        foreach (var other in absent)
            Assert.DoesNotContain(catalog.Codes, c => c.StartsWith(other + ".", StringComparison.Ordinal));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(new NpgsqlConnectionStringBuilder(host.ConnectionString).Database,
            new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()).Database);

        var stored = await db.Permissions.Where(p => p.Code.StartsWith(prefix)).ToListAsync();
        foreach (var permission in declared)
        {
            var row = stored.SingleOrDefault(p => p.Code == permission.Code);
            Assert.True(row is not null, $"Право «{permission.Code}» объявлено модулем, но в базу не попало.");
            Assert.True(row!.IsDeclared, $"Право «{permission.Code}» объявлено, но помечено как невыдаваемое.");
            Assert.Equal(permission.Gives, row.Gives);
        }

        return declared;
    }

    /// <summary>
    /// Права модуля либо стоят на двери, либо названы в <paramref name="doorless" /> с причиной (зеркало
    /// <see cref="EndpointGateInventoryTests" /> для модуля, выключенного на умолчательном хосте, AUTH-19).
    /// Список — храповик: запись права, у которого дверь появилась, обязана уйти.
    /// </summary>
    public static void DoorOrNamedDoorless(ModuleOnlyHost host, string module, IReadOnlyDictionary<string, string> doorless)
    {
        _ = host.CreateClient();

        var catalog = host.Services.GetRequiredService<PermissionCatalog>();
        var used = EndpointInventory.GatingPermissions(EndpointInventory.Routes(host.Services));

        var silent = catalog.Codes
            .Where(c => c.StartsWith(module + ".", StringComparison.Ordinal))
            .Where(c => !used.Contains(c) && !doorless.ContainsKey(c))
            .Order(StringComparer.Ordinal).ToList();

        Assert.True(silent.Count == 0,
            "Право модуля объявлено, но не стоит ни на одном адресе. Галка в редакторе ролей будет " +
            "выдаваться и не делать ничего. Поставьте право на дверь либо назовите здесь задачу, " +
            "которая её принесёт:\n  " + string.Join("\n  ", silent));

        var stale = doorless.Keys.Where(used.Contains).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "У этих прав двери уже есть — уберите записи:\n  " + string.Join("\n  ", stale));

        var unknown = doorless.Keys.Where(c => !catalog.Declares(c)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0,
            "В списке есть коды, которых модуль не объявляет:\n  " + string.Join("\n  ", unknown));
    }

    /// <summary>
    /// Системная роль получает из прав модуля РОВНО <paramref name="expected" /> — не «содержит»: лишнее право
    /// здесь — пункт меню, которого роль не должна была получить (AUTH-14). Признак модуля и его разделы —
    /// тоже по ответу <c>/api/account/access</c>.
    /// </summary>
    public static async Task RoleGetsExactlyAsync(
        ModuleOnlyHost host, IAppModule module, string role, bool opens, string[] expected)
    {
        var access = await AccessProbe.AccessAsync(await AccessProbe.SignInAsync(host, role, module.Code));

        var codes = access.Permissions.Where(c => c.StartsWith(module.Code + ".", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), codes);

        var entry = Assert.Single(access.Modules, m => m.Code == module.Code);
        Assert.Equal(opens, entry.Available);
        Assert.Equal(module.UsesSections, entry.Sections);
    }
}
