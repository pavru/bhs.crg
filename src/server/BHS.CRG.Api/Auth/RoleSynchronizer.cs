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

    /// <summary>Тип утверждения с описанием роли — оно стоит в редакторе рядом с названием.</summary>
    public const string SummaryClaim = "summary";

    /// <summary>
    /// Отметка «состав прав правил администратор» (ТЗ AUTH-5, issue #951).
    ///
    /// С этого момента состав роли принадлежит не коду: приводить его к объявленному при каждом
    /// старте значило бы отменять правку — молча и через перезапуск, то есть тогда, когда связать
    /// пропажу права с чем-либо уже невозможно.
    /// </summary>
    public const string EditedClaim = "edited";

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

            await SyncClaimsAsync(roles, role, definition, declared, logger);
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
        RoleDefinition definition,
        HashSet<string> declared,
        ILogger logger)
    {
        var existing = await roles.GetClaimsAsync(role);

        // Название и описание остаются за кодом даже у правленой роли: администратор меняет СОСТАВ
        // системной роли, а не то, что она означает. Переименование системной роли редактор
        // отклоняет прямо (см. RoleEditor), и возвращать здесь нечего.
        await SetSingleAsync(roles, role, existing, TitleClaim, definition.Title);
        await SetSingleAsync(roles, role, existing, SummaryClaim, definition.Summary);

        var current = existing
            .Where(c => c.Type == PermissionClaim)
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Состав правил администратор (AUTH-5) — код больше им не распоряжается.
        //
        // ⚠️ Кроме роли «все права». У неё состав не перечислен вовсе: он РАВЕН справочнику и
        // пополняется вместе с ним. Замри он однажды — право следующего выпуска и права нового
        // модуля не достались бы никому, включая администратора, и выглядело бы это как отказ
        // доступа всем сразу без единой причины в логе. Поэтому такая роль приводится к полному
        // составу всегда, даже с отметкой правки (её можно поставить и прямо в базе). Редактор
        // править её состав и не даёт — см. RoleEditor.
        if (existing.Any(c => c.Type == EditedClaim) && !definition.AllPermissions)
        {
            return;
        }

        var wanted = Wanted(definition, declared);

        foreach (var code in wanted.Except(current, StringComparer.OrdinalIgnoreCase))
            await roles.AddClaimAsync(role, new Claim(PermissionClaim, code));

        foreach (var code in current.Except(wanted, StringComparer.OrdinalIgnoreCase))
        {
            await roles.RemoveClaimAsync(role, new Claim(PermissionClaim, code));
            logger.LogInformation(
                "Право {Code} убрано из системной роли {Role}: его больше нет в объявленном составе",
                code, role.Name);
        }
    }

    private static async Task SetSingleAsync(
        RoleManager<IdentityRole<Guid>> roles, IdentityRole<Guid> role,
        IList<Claim> existing, string type, string? value)
    {
        var stored = existing.FirstOrDefault(c => c.Type == type);
        value = (value ?? "").Trim();

        if (stored is null)
        {
            if (value.Length > 0) await roles.AddClaimAsync(role, new Claim(type, value));
            return;
        }

        if (stored.Value == value) return;
        await roles.RemoveClaimAsync(role, stored);
        if (value.Length > 0) await roles.AddClaimAsync(role, new Claim(type, value));
    }
}
