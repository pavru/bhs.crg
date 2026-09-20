using System.Security.Claims;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace BHS.CRG.Api.Auth;

/// <summary>
/// Действующие права владельца токена — из базы, через кэш (ТЗ AUTH-6).
///
/// Кэш нужен потому, что права спрашиваются на КАЖДОМ запросе к закрытому адресу, а считаются они
/// по ролям и их составу — это несколько обращений к базе. Без кэша проверка прав стоила бы дороже
/// самого запроса.
///
/// ⚠️ Ключ кэша — пользователь И отметка безопасности из токена. Это и есть «сброс кэша при смене
/// ролей» (AUTH-7): смена ролей обновляет отметку, у следующего токена ключ другой, и права
/// читаются заново. Отдельного вызова «сбросить» нет намеренно — его можно забыть позвать, а
/// забытый сброс выглядит как работающая система со старыми правами. Здесь забыть нечего: ключ
/// приходит из того же токена, который уже проверяется по отметке.
///
/// Прежние записи уходят по сроку. Срок короткий: он же — предел, сколько живёт правка состава
/// прав РОЛИ, сделанная в базе мимо приложения. Смена ролей пользователя ждать его не обязана.
/// </summary>
public sealed class PermissionCache(IMemoryCache cache, IServiceScopeFactory scopes) : IUserPermissions
{
    /// <summary>
    /// Сколько живёт посчитанный набор прав. Секунды, а не минуты: это окно, в котором действуют
    /// старые права после правки состава роли напрямую в базе.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyCollection<string>> ForAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = principal.FindFirst("sub")?.Value;
        var stamp = principal.FindFirst(JwtTokens.SecurityStampClaim)?.Value;

        // Токен без «кто это» до ворот не доходит — его отклоняет проверка токена. Если всё же
        // дошёл, прав у него нет: пустой набор закрывает все двери, а не открывает их.
        if (userId is null || stamp is null) return [];

        var key = $"perm|{userId}|{stamp}";
        if (cache.TryGetValue(key, out IReadOnlyCollection<string>? cached) && cached is not null)
            return cached;

        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var effective = scope.ServiceProvider.GetRequiredService<EffectivePermissions>();

        var user = await users.FindByIdAsync(userId);
        var granted = user is null ? [] : await effective.OfAsync(user);

        cache.Set(key, granted, Lifetime);
        return granted;
    }
}
