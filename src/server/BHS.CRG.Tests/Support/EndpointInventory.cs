using BHS.CRG.Modules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Support;

/// <summary>
/// Общая опора инвентаризации адресов (ТЗ AUTH-9): что считать адресом, что считать воротами и какие
/// права уже стоят на дверях.
///
/// <para>Вынесено из <see cref="Integration.EndpointGateInventoryTests" />, когда инвентаризовать
/// понадобилось ВТОРОЙ хост — установку с другим составом модулей (issue #1068). Своя копия этих
/// пяти правил у второго сторожа означала бы, что «ворота» у них разойдутся молча: тот же урок, что
/// с четырьмя копиями охвата исходников клиента (ревью PR #1067). Корзины исключений здесь НЕ
/// живут — они у каждого сторожа свои, потому что и решения в них разные.</para>
/// </summary>
internal static class EndpointInventory
{
    /// <summary>
    /// Адреса живого приложения.
    ///
    /// <paramref name="includeRefusals" /> — брать ли отказы выключенных модулей
    /// (<see cref="DisabledModuleEndpoint" />). По умолчанию НЕ брать: за отказом нет ни данных, ни
    /// служб, он отвечает 501 «модуль не подключён» и анонимен намеренно (ТЗ OVW-10, AUTH-15,
    /// AUTH-19), а сторож у него свой. Записью в корзине путей это выразить нельзя: строку
    /// «/api/costs» пришлось бы завести при появлении модуля (её забудут) и убрать при его включении
    /// — у включённого модуля этих адресов нет вовсе, и запись, оставшись, стала бы ложной.
    ///
    /// ⚠️ Исключение обязано быть проверяемым, иначе оно дыра: что пометка стоит только на отказах
    /// выключенного модуля, сверяет <c>Refusal_endpoints_are_refusals_of_disabled_modules</c>.
    /// </summary>
    internal static IReadOnlyList<RouteEndpoint> Routes(IServiceProvider services, bool includeRefusals = false) =>
        [.. services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => includeRefusals || e.Metadata.GetMetadata<DisabledModuleEndpoint>() is null)];

    /// <summary>Ворота — политика права или политика модуля. «Вошёл» и роль воротами не считаются.</summary>
    internal static bool IsGated(Endpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a =>
            a.Policy is { } p &&
            (p.StartsWith(AppPolicies.PermissionPrefix, StringComparison.OrdinalIgnoreCase)
             || p.StartsWith(AppPolicies.ModulePrefix, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Права, которые стоят хотя бы на одной двери.</summary>
    internal static HashSet<string> GatingPermissions(IEnumerable<RouteEndpoint> routes) =>
        routes
            .SelectMany(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>())
            .Select(a => a.Policy)
            .Where(p => p is not null
                && p.StartsWith(AppPolicies.PermissionPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => p![AppPolicies.PermissionPrefix.Length..].Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static string Route(RouteEndpoint endpoint) =>
        "/" + (endpoint.RoutePattern.RawText ?? string.Empty).Trim('/');

    /// <summary>Совпадение по границе сегмента: <c>/api/plans</c> не накрывает <c>/api/plans-archive</c>.</summary>
    internal static bool Matches(string route, string prefix) =>
        route.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || route.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    internal static string Verbs(Endpoint endpoint) =>
        string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"]);
}
