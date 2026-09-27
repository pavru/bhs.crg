using System.Security.Claims;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Identity;

namespace BHS.CRG.Api.Auth;

/// <summary>
/// Откуда берётся параметр доступа к строкам (ТЗ CORE-24.1, issue #965).
///
/// <para>Живёт в Api по той же причине, что <see cref="Notifications.PermissionAudience" />: права и
/// состав включённых модулей объявлены здесь, а подсистема наборов данных лежит ниже по стеку и о
/// них не знает. Ей достаточно набора ключей — чьё это дело, считается тут.</para>
///
/// <para><b>Два входа, одно правило.</b> <see cref="ForAsync" /> — для запроса: права берутся через
/// тот же кэш, которым закрыты адреса (<see cref="PermissionCache" />), поэтому чтение строк не
/// стоит дороже собственной проверки прав, а отозванное право закрывает набор в том же окне, что и
/// дверь. <see cref="ForUserAsync" /> — для задания, запущенного человеком: запроса нет, есть
/// идентификатор автора, и права считаются из базы напрямую.</para>
///
/// <para>⚠️ Ключи считаются ТЕМ ЖЕ приёмом, что аудитория уведомлений (ТЗ AUTH-13.1): права плюс
/// коды доступных модулей одним набором. Набор требует либо право, либо код модуля — у библиотеки
/// документов качества своего права нет, — и два разных способа сверки означали бы два правила
/// доступа к одним данным.</para>
/// </summary>
public sealed class DataAccessResolver(
    IUserPermissions permissions,
    IServiceScopeFactory scopes,
    ModuleRegistry modules) : IDataAccessResolver
{
    /// <summary>Параметр доступа владельца токена.</summary>
    public async Task<DataAccess> ForAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var granted = await permissions.ForAsync(principal, ct);
        var id = UserIdOf(principal);

        // Токен без «кто это» до сюда не доходит — его отклоняет проверка токена. Если всё же дошёл,
        // это НЕ повод считать читателя системой: у системы отказ с иной причиной, и он увёл бы
        // разбор к фоновым заданиям. Пользователь без прав — пустой набор ключей, все наборы закрыты.
        if (id is null)
            return DataAccess.Of(Guid.Empty, "неизвестный пользователь", [], EnabledModules());

        return DataAccess.Of(id.Value, NameOf(principal, id.Value), Keys(granted), EnabledModules());
    }

    /// <summary>
    /// То же для запроса, до которого принципал не доезжает параметром: инструменты и ресурсы MCP
    /// берут пользователя из того же JWT через <see cref="IHttpContextAccessor" /> (ТЗ AUTH-12.1).
    /// </summary>
    public Task<DataAccess> ForRequestAsync(IHttpContextAccessor http, CancellationToken ct) =>
        ForAsync(
            http.HttpContext?.User
            // Не «система»: у неё отказ с другой причиной, и он увёл бы разбор к фоновым заданиям.
            ?? throw new ForbiddenException(
                "Не удалось определить пользователя: запрос без действительного токена. " +
                "Строки наборов отдаются в правах спрашивающего, и читать их «ничьими» правами нельзя."),
            ct);

    /// <inheritdoc />
    public async Task<DataAccess> ForUserAsync(Guid userId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var effective = scope.ServiceProvider.GetRequiredService<EffectivePermissions>();

        var user = await users.FindByIdAsync(userId.ToString())
            // Отказ, а не «система»: задание, автора которого больше нет, обязано остановиться.
            // Продолжить его правами системы значило бы выдать данные от имени того, кого удалили.
            ?? throw new ConflictException(
                $"Задача запущена пользователем {userId}, а его учётной записи больше нет: " +
                "в чьих правах читать данные — неизвестно, и продолжать нельзя.");

        return DataAccess.Of(userId, user.UserName ?? userId.ToString(),
            Keys(await effective.OfAsync(user)), EnabledModules());
    }

    private IReadOnlyList<string> EnabledModules() => [.. modules.Enabled.Select(m => m.Code)];

    private List<string> Keys(IReadOnlyCollection<string> granted)
    {
        var keys = new List<string>(granted);
        // Правило «доступ к модулю = есть хоть одно его право» живёт в ModuleAccess одно на всю
        // систему (ТЗ AUTH-8.1): свой второй экземпляр здесь разошёлся бы с воротами адресов.
        foreach (var module in modules.Enabled)
            if (ModuleAccess.IsOpen(module.Code, granted))
                keys.Add(module.Code);
        return keys;
    }

    private static Guid? UserIdOf(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst("sub")?.Value
                      ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
            ? id
            : null;

    private static string NameOf(ClaimsPrincipal principal, Guid fallback) =>
        principal.Identity?.Name
        ?? principal.FindFirst(ClaimTypes.Name)?.Value
        ?? fallback.ToString();
}
