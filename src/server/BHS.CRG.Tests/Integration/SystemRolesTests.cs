using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Системные роли: заводятся при установке, держат объявленный состав прав и не теряют главного
/// (issue #945, ТЗ AUTH-2, AUTH-3, AUTH-4).
///
/// Без стартового набора ролей администратору при первой установке нечего выдать, кроме
/// «Администратора», — и все сотрудники получают полный доступ «пока не разберёмся».
/// </summary>
[Collection("Integration")]
public class SystemRolesTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task All_system_roles_exist_after_startup()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        foreach (var definition in SystemRoles.All)
            Assert.True(
                await roles.RoleExistsAsync(definition.Name),
                $"Системная роль «{definition.Name}» ({definition.Title}) не создана при старте.");
    }

    /// <summary>
    /// «Администратор» получает всё объявленное. Перечисление прав у него разошлось бы со
    /// справочником на первом же новом праве — и разошлось бы молча.
    /// </summary>
    [Fact]
    public async Task Administrator_gets_every_declared_permission()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var catalog = fixture.Services.GetRequiredService<PermissionCatalog>();

        var admin = await roles.FindByNameAsync(SystemRoles.Admin);
        var granted = (await roles.GetClaimsAsync(admin!))
            .Where(c => c.Type == RoleSynchronizer.PermissionClaim)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var code in catalog.Codes)
            Assert.Contains(code, granted);
    }

    /// <summary>
    /// Право «управлять пользователями» возвращается «Администратору» при каждом старте, даже если
    /// его сняли: экземпляр без этого права чинится только руками в базе.
    /// </summary>
    [Fact]
    public async Task Administrator_cannot_stay_without_user_management()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var catalog = fixture.Services.GetRequiredService<PermissionCatalog>();

        var admin = await roles.FindByNameAsync(SystemRoles.Admin);
        await roles.RemoveClaimAsync(admin!, new Claim(RoleSynchronizer.PermissionClaim, SystemRoles.AdminCannotLose));

        await RoleSynchronizer.SyncAsync(roles, catalog, NullLogger.Instance);

        var granted = (await roles.GetClaimsAsync((await roles.FindByNameAsync(SystemRoles.Admin))!))
            .Where(c => c.Type == RoleSynchronizer.PermissionClaim)
            .Select(c => c.Value);

        Assert.Contains(SystemRoles.AdminCannotLose, granted);
    }

    /// <summary>
    /// Состав системной роли ПРИВОДИТСЯ к объявленному, а не дополняется: право, убранное из
    /// состава в коде, иначе осталось бы выданным навсегда на всех установках, где роль успела
    /// появиться.
    /// </summary>
    [Fact]
    public async Task Extra_permission_on_a_system_role_is_taken_back()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var catalog = fixture.Services.GetRequiredService<PermissionCatalog>();

        var estimator = await roles.FindByNameAsync("Estimator");
        await roles.AddClaimAsync(estimator!, new Claim(RoleSynchronizer.PermissionClaim, "core.users.manage"));

        await RoleSynchronizer.SyncAsync(roles, catalog, NullLogger.Instance);

        var granted = (await roles.GetClaimsAsync((await roles.FindByNameAsync("Estimator"))!))
            .Where(c => c.Type == RoleSynchronizer.PermissionClaim)
            .Select(c => c.Value);

        Assert.DoesNotContain("core.users.manage", granted);
    }

    /// <summary>
    /// Удалённая системная роль возвращается при следующем старте — вместе с составом прав.
    ///
    /// Это и есть сегодняшняя защита «системную роль нельзя потерять»: запретить удаление негде,
    /// удаления ролей в API не существует. Когда появится редактор ролей, там встанет и запрет;
    /// до тех пор восстановление — не утешение, а рабочая защита, и она проверяема.
    /// </summary>
    [Fact]
    public async Task Deleted_system_role_comes_back_on_next_start()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var catalog = fixture.Services.GetRequiredService<PermissionCatalog>();

        var installer = await roles.FindByNameAsync("Installer");
        await roles.DeleteAsync(installer!);
        Assert.False(await roles.RoleExistsAsync("Installer"));

        await RoleSynchronizer.SyncAsync(roles, catalog, NullLogger.Instance);

        Assert.True(await roles.RoleExistsAsync("Installer"));
    }

    /// <summary>Повторный старт не задваивает утверждения: сверка идёт при каждом запуске.</summary>
    [Fact]
    public async Task Sync_is_idempotent()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var catalog = fixture.Services.GetRequiredService<PermissionCatalog>();

        await RoleSynchronizer.SyncAsync(roles, catalog, NullLogger.Instance);
        var first = (await roles.GetClaimsAsync((await roles.FindByNameAsync("Supplier"))!)).Count;

        await RoleSynchronizer.SyncAsync(roles, catalog, NullLogger.Instance);
        var second = (await roles.GetClaimsAsync((await roles.FindByNameAsync("Supplier"))!)).Count;

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Действующие права — объединение прав всех ролей пользователя (AUTH-3), а не первой из них.
    /// </summary>
    [Fact]
    public async Task Effective_permissions_are_the_union_of_roles()
    {
        _ = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var permissions = scope.ServiceProvider.GetRequiredService<EffectivePermissions>();

        var user = new ApplicationUser { UserName = "union@example.test", Email = "union@example.test" };
        await users.CreateAsync(user, "Union-Pass-1");
        await users.AddToRoleAsync(user, "Estimator");
        await users.AddToRoleAsync(user, "Accountant");

        var effective = await permissions.OfAsync(user);

        Assert.Contains("core.worktypes.edit", effective);   // от сметчика
        Assert.Contains("core.period.close", effective);     // от бухгалтера
        Assert.DoesNotContain("core.users.manage", effective);

        await users.DeleteAsync(user);
    }
}
