using BHS.CRG.Application.Notifications;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;

namespace BHS.CRG.Api.Notifications;

/// <summary>
/// Аудитория уведомлений по правам (ТЗ AUTH-13, CORE-27) — реализация для ядра.
///
/// Живёт в Api, потому что права и модули объявлены здесь, а подсистема уведомлений — ниже по
/// стеку и о них не знает. Ей достаточно набора ключей: чьё это дело — считается тут.
///
/// Следствие, которое стоит знать: уведомления ВЫКЛЮЧЕННОГО модуля не видит никто — его код в
/// ключи не попадает. Это то же правило, что и у его адресов, отвечающих отказом с причиной
/// (AUTH-19): модуль выключен — значит его дел на экране нет.
/// </summary>
public sealed class PermissionAudience(
    IMemoryCache cache,
    IServiceScopeFactory scopes,
    PermissionCatalog catalog,
    ModuleRegistry modules) : INotificationAudience
{
    /// <summary>
    /// Сколько живёт посчитанный набор ключей. Столько же, сколько права у ворот
    /// (<see cref="PermissionCache.Lifetime" />), и по той же причине: колокольчик опрашивают
    /// поллингом, а отозванное право не должно возвращать чужие уведомления надолго.
    ///
    /// ⚠️ Ключ кэша — пользователь, без отметки безопасности: здесь нет токена, список читает
    /// подсистема уведомлений по идентификатору. Поэтому смена ролей действует не мгновенно, как на
    /// воротах, а в пределах этого срока — для списка уведомлений это допустимо.
    /// </summary>
    private static readonly TimeSpan Lifetime = PermissionCache.Lifetime;

    public async Task<IReadOnlyCollection<string>> KeysForAsync(Guid userId, CancellationToken ct)
    {
        var key = $"notify-audience|{userId}";
        if (cache.TryGetValue(key, out IReadOnlyCollection<string>? cached) && cached is not null)
            return cached;

        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var effective = scope.ServiceProvider.GetRequiredService<EffectivePermissions>();

        var user = await users.FindByIdAsync(userId.ToString());
        IReadOnlyCollection<string> granted = user is null ? [] : await effective.OfAsync(user);

        // Права плюс коды доступных модулей — одним набором. Доступ к модулю считает
        // ModuleAccess: правило «есть хоть одно право модуля» живёт там одно на всю систему.
        var keys = new HashSet<string>(granted, StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules.Enabled)
            if (ModuleAccess.IsOpen(module.Code, granted))
                keys.Add(module.Code);

        cache.Set(key, (IReadOnlyCollection<string>)keys, Lifetime);
        return keys;
    }

    public void EnsureDeclared(string audience)
    {
        if (catalog.Declares(audience) || modules.IsEnabled(audience)) return;

        throw new ArgumentException(
            $"Уведомление адресовано «{audience}» — такого права нет в каталоге, и такой модуль не включён. " +
            "Адресат, которого не существует, не получит ничего, а отправка будет выглядеть удавшейся.",
            nameof(audience));
    }
}
