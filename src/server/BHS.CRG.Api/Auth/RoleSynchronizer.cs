using System.Security.Claims;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Identity;

namespace BHS.CRG.Api.Auth;

/// <summary>
/// Создаёт системные роли и держит их состав прав в согласии с объявленным (ТЗ AUTH-2, AUTH-4).
///
/// Права роли хранятся утверждениями роли (<c>AspNetRoleClaims</c>) — таблицей, которая в проекте
/// была заведена с первой миграции и всё это время пустовала. Отдельная своя таблица «роль — право»
/// означала бы второй механизм ролей рядом с уже существующим.
///
/// ⚠️ Состав системной роли при старте ПРИВОДИТСЯ к объявленному, а не дополняется. Иначе право,
/// убранное из состава роли в коде, осталось бы выданным навсегда — на всех установках, где роль
/// успела появиться. Роли, заведённые администратором, не трогаются вовсе: они не наши.
/// </summary>
public static class RoleSynchronizer
{
    /// <summary>Тип утверждения, которым право записано у роли.</summary>
    public const string PermissionClaim = "perm";

    /// <summary>Тип утверждения с человеческим названием роли.</summary>
    public const string TitleClaim = "title";

    public static async Task SyncAsync(
        RoleManager<IdentityRole<Guid>> roles, PermissionCatalog catalog, ILogger logger)
    {
        var declared = catalog.Codes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in SystemRoles.All)
        {
            var role = await roles.FindByNameAsync(definition.Name);
            if (role is null)
            {
                role = new IdentityRole<Guid>(definition.Name);
                var created = await roles.CreateAsync(role);
                if (!created.Succeeded)
                    throw new InvalidOperationException(
                        $"Не удалось создать системную роль «{definition.Name}»: " +
                        string.Join("; ", created.Errors.Select(e => e.Description)));
            }

            var wanted = Wanted(definition, declared);
            await SyncClaimsAsync(roles, role, wanted, definition.Title, logger);
        }
    }

    /// <summary>
    /// Какие права роль обязана иметь. Неизвестные коды отбрасываются молча: состав роли назван
    /// целиком, включая права модулей, которых в этой сборке нет, — они придут вместе с модулем.
    /// </summary>
    private static HashSet<string> Wanted(RoleDefinition definition, HashSet<string> declared)
    {
        if (definition.AllPermissions) return [.. declared];

        var wanted = definition.Permissions.Where(declared.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Администратор не может лишиться управления пользователями — даже по недосмотру в
        // объявлении состава. Экземпляр без этого права чинится только руками в базе.
        if (string.Equals(definition.Name, SystemRoles.Admin, StringComparison.OrdinalIgnoreCase)
            && declared.Contains(SystemRoles.AdminCannotLose))
            wanted.Add(SystemRoles.AdminCannotLose);

        return wanted;
    }

    private static async Task SyncClaimsAsync(
        RoleManager<IdentityRole<Guid>> roles,
        IdentityRole<Guid> role,
        HashSet<string> wanted,
        string title,
        ILogger logger)
    {
        var existing = await roles.GetClaimsAsync(role);

        var current = existing
            .Where(c => c.Type == PermissionClaim)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var code in wanted.Except(current, StringComparer.OrdinalIgnoreCase))
            await roles.AddClaimAsync(role, new Claim(PermissionClaim, code));

        foreach (var code in current.Except(wanted, StringComparer.OrdinalIgnoreCase))
        {
            await roles.RemoveClaimAsync(role, new Claim(PermissionClaim, code));
            logger.LogInformation(
                "Право {Code} убрано из системной роли {Role}: его больше нет в объявленном составе",
                code, role.Name);
        }

        var storedTitle = existing.FirstOrDefault(c => c.Type == TitleClaim);
        if (storedTitle is null)
            await roles.AddClaimAsync(role, new Claim(TitleClaim, title));
        else if (storedTitle.Value != title)
        {
            await roles.RemoveClaimAsync(role, storedTitle);
            await roles.AddClaimAsync(role, new Claim(TitleClaim, title));
        }
    }
}
