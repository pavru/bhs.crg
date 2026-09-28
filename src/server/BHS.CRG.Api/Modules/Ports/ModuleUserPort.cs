using BHS.CRG.Application.Activity;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Кто действует и что ему можно — для модуля (ТЗ AUTH-6).
///
/// <para>Собран из того же, чем это отвечено ядру: автор берётся у <see cref="IActivityActor" />
/// (значит, имя в журнале и имя на экране модуля не разойдутся), а права — у того же счётчика,
/// который держит ворота. Второй способ определить пользователя означал бы, что модуль считает
/// «вошёл» не так, как двери, — и разница нашлась бы на отозванном доступе.</para>
///
/// <para>⚠️ Вне запроса принципала нет, и права не спрашиваются вовсе: ответ — «прав нет». Не
/// «можно всё» и не отказ: фоновая операция обязана либо взять права поставившего из задачи, либо
/// обойтись без них.</para>
/// </summary>
public sealed class ModuleUserPort(
    IActivityActor actor, IUserPermissions permissions, IHttpContextAccessor http) : IModuleUser
{
    public Guid? Id => actor.Current.Id;

    public string Name => actor.Current.Name;

    public async Task<bool> HasAsync(string permission, CancellationToken ct = default) =>
        (await PermissionsAsync(ct)).Contains(permission);

    public async Task<IReadOnlyCollection<string>> PermissionsAsync(CancellationToken ct = default)
    {
        var user = http.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true) return [];

        // Кэш на запрос — внутри счётчика: спрашивать права на каждом решении экрана дешевле, чем
        // собирать их у вызывающего в переменную, которую потом забудут обновить.
        return await permissions.ForAsync(user, ct);
    }
}
