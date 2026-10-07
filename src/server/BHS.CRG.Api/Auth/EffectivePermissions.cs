using System.Security.Claims;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
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
///
/// <para><b>Составное «читать всё» раскрывается ЗДЕСЬ</b> (задача A3 этапа 2, issue #1074, ТЗ
/// AUTH-5.2): в наборе, который отсюда выходит, уже стоят права чтения включённых модулей. Место
/// выбрано одно на всех, кто спрашивает права, — ворота адресов и модулей, навигация клиента,
/// адресаты уведомлений, доступ к строкам наборов данных, ресурсы MCP. Раскрытие у ворот дало бы
/// «Руководителю» адрес и не дало бы пункта меню: набор прав у этих потребителей один, и разойтись
/// им нельзя.</para>
/// </summary>
public sealed class EffectivePermissions(
    UserManager<ApplicationUser> users, RoleManager<IdentityRole<Guid>> roles, PermissionCatalog catalog)
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

        return catalog.Expand(result);
    }

    /// <summary>
    /// Есть ли право — с учётом раскрытия «читать всё»: владелец составного права имеет и те права
    /// чтения, что объявили входящими включённые модули (AUTH-5.2).
    /// </summary>
    public async Task<bool> HasAsync(ApplicationUser user, string permission) =>
        (await OfAsync(user)).Contains(permission);
}
