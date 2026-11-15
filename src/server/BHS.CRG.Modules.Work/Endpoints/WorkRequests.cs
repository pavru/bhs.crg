using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>Версия, по которой собрана правка (<c>If-Match</c>), — одним правилом для назначений и отчётов.</summary>
internal static class SeenVersions
{
    /// <summary>
    /// Версия из заголовка. Не названа — 400 со словами, где её взять; испорчена — 400 <see cref="SeenVersion.Malformed" />.
    /// </summary>
    /// <param name="what">Чья версия, в родительном падеже: «назначения», «отчёта».</param>
    public static string Required(HttpRequest request, string what)
    {
        if (!SeenVersion.TryRead(request, out var seen)) throw new InvalidRequestException(SeenVersion.Malformed);
        return seen ?? throw new InvalidRequestException(
            $"Не названа версия {what}, по которой собрана правка (заголовок {SeenVersion.Header}) — она приходит в " +
            "ответе чтения полем «version». Без неё правка записалась бы поверх чужой.");
    }
}

/// <summary>
/// Сотрудник учётной записи спрашивающего (ТЗ WORK-47) — одним правилом для «моих строек» и своих отчётов. Пустой
/// ответ называет причину: «справочника нет» и «не связана» различаются только вопросом о справочнике, а выход из
/// них разный.
/// </summary>
internal static class WorkAccounts
{
    public const string NotLinked = "not-linked";

    /// <summary>Сотрудник или причина его отсутствия: <see cref="IModuleEmployees.NotSetUpCode" /> либо <see cref="NotLinked" />.</summary>
    public static async Task<(ModuleEmployee? Me, string? Reason)> OfUserAsync(
        IModuleUser user, IModuleEmployees employees, CancellationToken ct)
    {
        if (user.Id is { } userId && await employees.OfAccountAsync(userId, ct) is { } me) return (me, null);
        return (null, await employees.GetAsync([], ct) is null ? IModuleEmployees.NotSetUpCode : NotLinked);
    }
}
