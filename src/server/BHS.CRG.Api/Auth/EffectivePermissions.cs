using System.Security.Claims;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace BHS.CRG.Api.Auth;

/// <summary>
/// Действующие права пользователя — объединение прав всех его ролей (ТЗ AUTH-3).
///
/// Объединение, а не пересечение, и отрицательных прав нет: «запретить, несмотря на роль» делает
/// разбор доступа неразрешимым глазами — чтобы ответить, есть ли у человека право, пришлось бы
/// держать в голове порядок ролей и перекрытия. Одно правило «где-то дано — значит дано» читается
/// с первого раза, а сузить доступ можно, сняв роль.
///
/// ⚠️ Права считаются ИЗ БАЗЫ при проверке, а не берутся из токена (AUTH-6): права в токене
/// устаревают ровно тогда, когда это опаснее всего — при отзыве доступа. Кэш появится вместе с
/// политиками; пока считается напрямую, и это честнее преждевременного кэша, который некому
/// сбрасывать.
/// </summary>
public sealed class EffectivePermissions(
    UserManager<ApplicationUser> users, RoleManager<IdentityRole<Guid>> roles)
{
    public async Task<IReadOnlyCollection<string>> OfAsync(ApplicationUser user)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var roleName in await users.GetRolesAsync(user))
        {
            var role = await roles.FindByNameAsync(roleName);
            if (role is null) continue;   // роль удалили, а связь осталась — не повод падать

            foreach (var claim in await roles.GetClaimsAsync(role))
                if (claim.Type == RoleSynchronizer.PermissionClaim)
                    result.Add(claim.Value);
        }

        return result;
    }

    /// <summary>
    /// Есть ли право. <c>*.read.all</c> здесь НЕ раскрывается: что в него входит, объявляет каждый
    /// модуль, и раскрытие появится вместе со вторым модулем (AUTH-5.2). До тех пор владелец
    /// составного права имеет именно его — и ни одного чужого права по умолчанию.
    /// </summary>
    public async Task<bool> HasAsync(ApplicationUser user, string permission) =>
        (await OfAsync(user)).Contains(permission);
}
