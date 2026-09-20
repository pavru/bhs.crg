using BHS.CRG.Modules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Инвентаризация адресов (issue #947, ТЗ AUTH-9/AUTH-10/AUTH-11).
///
/// Сторож обходит адреса ЖИВОГО приложения и требует, чтобы каждый незакрытый адрес попал ровно в
/// одну названную корзину: публичный, личный или долг. Адрес, не попавший никуда, роняет прогон с
/// перечислением — новый адрес нельзя завести молча.
///
/// Почему сторож, а не обещание. Забытые ворота — самая тихая из поломок прав: адрес отвечает
/// данными, а не отказом, и выглядит это как исправно работающая система. Заметить такое можно
/// только перечислением, и перечислять обязана машина: адресов больше двухсот, и человек,
/// проверивший их однажды, назавтра проверяет их заново.
///
/// ⚠️ Списки ниже — ратчет, а не механизм. В работе приложения их не читает никто: ворота стоят в
/// коде адресов. Поэтому запись, потерявшая свои адреса, — тоже отказ: список, разошедшийся с
/// кодом, перестаёт что-либо утверждать, продолжая выглядеть утверждением.
/// </summary>
[Collection("Integration")]
public class EndpointGateInventoryTests(IntegrationTestFixture fixture)
{
    /// <summary>
    /// Открыты любому, без входа. Каждая запись — решение, а не наблюдение: сюда попадает только
    /// то, что обязано работать ДО входа в систему.
    /// </summary>
    private static readonly Dictionary<string, string> Public = new()
    {
        ["/api/auth"] = "вход, регистрация, обновление токена, восстановление пароля — до входа других путей нет",
        ["/api/version"] = "номер версии виден на странице входа; хеш сборки и дата — только вошедшему",
    };

    /// <summary>
    /// Личные адреса: вошедшему доступно СВОЁ и только своё, отдельного права не нужно.
    ///
    /// Это третья корзина, которой в ТЗ нет, и назвать её пришлось здесь. Право «читать свои
    /// уведомления» было бы правом, которое выдаётся всем и никогда не снимается, — записью,
    /// которая ничего не разграничивает, но которую придётся сопровождать в каждой роли.
    ///
    /// ⚠️ Граница корзины жёсткая: адрес пускают сюда, только если он отвечает данными САМОГО
    /// пользователя, и это видно по коду обработчика. Как только адрес показывает чужое — он уходит
    /// под право. Поэтому <c>/api/notifications/health</c> записан в долг отдельной строкой: он
    /// лежит среди личных уведомлений, но отвечает состоянием системы, общим для всех.
    /// </summary>
    private static readonly Dictionary<string, string> Personal = new()
    {
        ["/api/account"] = "свой профиль, свой пароль, своя почта — обработчики берут пользователя из принципала",
        ["/api/jobs"] = "свои фоновые задачи; чужая задача отвечает 404 (IJobService сверяет владельца)",
        ["/api/notifications"] = "свои уведомления и отметки о прочтении; кроме /health — он в долге",
    };

    /// <summary>
    /// Долг: группы, где стоит только «пользователь вошёл» либо роль «Admin», то есть любой
    /// вошедший имеет полный доступ (ТЗ AUTH-11). Список опустошается вторым PR этой задачи.
    ///
    /// Долг записан поимённо НАРОЧНО. Пока он существовал фразой «у 31 группы стоит только проверка
    /// входа», его нельзя было ни пересчитать, ни закрывать по частям, ни заметить, что он вырос.
    /// Справа от каждой записи — право, под которое группа уйдёт; это и есть план второго PR,
    /// написанный там, где его проверяет машина.
    /// </summary>
    private static readonly Dictionary<string, string> Debt = new()
    {
        ["/api/attachments"] = "→ решить: вложения и выдача файла из хранилища по пути (ТЗ AUTH-12)",
        ["/api/backup"] = "→ право обслуживания экземпляра (в ТЗ такого права ещё нет)",
        ["/api/bug-reports"] = "→ личное для своих обращений, право — для чужих и для разбора",
        ["/api/catalog"] = "→ справочник сущностей; переезжает в ядро вместе с #961",
        ["/api/common-data"] = "→ общие данные; тот же переезд, что и каталог",
        ["/api/constructions"] = "→ core.constructions.edit (чтение — всем вошедшим?)",
        ["/api/datasets"] = "→ наборы данных: право чтения и право настройки (в ТЗ ещё не названы)",
        ["/api/document-sets"] = "→ id.document.read / id.document.edit",
        ["/api/document-types"] = "→ core.types.edit на правку, чтение схемы нужно всем",
        ["/api/email"] = "→ право обслуживания экземпляра",
        ["/api/enum-types"] = "→ core.types.edit",
        ["/api/generate"] = "→ id.document.generate",
        ["/api/maintenance"] = "→ право обслуживания экземпляра",
        ["/api/notifications/health"] = "→ состояние системы и внешних движков — не личное",
        ["/api/objects"] = "→ разбор строки в объект каталога: доступ к данным каталога",
        ["/api/observations"] = "→ core.reconciliation.run",
        ["/api/primitive-types"] = "→ core.types.edit",
        ["/api/recognition-profiles"] = "→ core.recognition.settings",
        ["/api/reconciliations"] = "→ core.reconciliation.run",
        ["/api/sections"] = "→ core.constructions.edit",
        ["/api/settings"] = "→ core.recognition.settings и право обслуживания экземпляра",
        ["/api/subscriptions"] = "→ #949: аудитория уведомлений по правам; сейчас любой вошедший подписывает любого и видит чужие адреса",
        ["/api/system"] = "→ право обслуживания экземпляра (проверка и установка обновлений)",
        ["/api/tags"] = "→ реестр функциональных тэгов: устройство типов, не личные данные",
        ["/api/template-assets"] = "→ id.config.edit",
        ["/api/templates"] = "→ id.config.edit",
        ["/api/typst-userlib"] = "→ id.config.edit",
        ["/mcp"] = "→ #948: модуль и право у каждого инструмента MCP (ТЗ AUTH-12.1)",
        ["/openapi"] = "→ решить: описание API отдаётся БЕЗ входа и перечисляет все адреса; " +
                       "устройство системы этим не выдаётся (репозиторий публичный), но состав " +
                       "включённых у заказчика модулей — выдаётся",
    };

    [Fact]
    public void Every_endpoint_is_gated_declared_public_or_written_into_the_debt()
    {
        var homeless = Homeless(Routes(), Public, Personal, Debt);

        Assert.True(homeless.Count == 0,
            "Адреса без ворот и без записи в списке. Каждому нужно либо право (политика perm: или " +
            "module:), либо строка в Public/Personal/Debt с объяснением:\n  " +
            string.Join("\n  ", homeless));

        var touched = Touched(Routes(), Public, Personal, Debt);
        var stale = new[] { Public, Personal, Debt }.SelectMany(b => b.Keys)
            .Where(k => !touched.Contains(k)).Order().ToList();

        Assert.True(stale.Count == 0,
            "Записи, под которые больше нет ни одного незакрытого адреса. Список разошёлся с кодом " +
            "и перестал что-либо утверждать — уберите их:\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    /// Проверка проверяется нарушением (ТЗ AUTH-9): убираем из корзин список долга — и сторож
    /// обязан заговорить, назвав адреса, закрытые одним входом.
    ///
    /// Без этого «зелено» основного теста означало бы лишь то, что сторож ничего не смотрит. Сам
    /// список долга для этого и годится: пока он не пуст, в приложении заведомо есть адреса, на
    /// которых сторож должен сработать. Опустеет долг — тест останется честным: он упадёт, и упадёт
    /// по делу, потому что проверять срабатывание станет не на чем.
    /// </summary>
    [Fact]
    public void Guard_speaks_when_a_gate_is_missing()
    {
        var withoutDebt = Homeless(Routes(), Public, Personal);

        Assert.NotEmpty(withoutDebt);
        Assert.All(withoutDebt, line => Assert.Contains("/", line));
    }

    /// <summary>Адреса, не закрытые воротами и не найденные ни в одной корзине.</summary>
    private static List<string> Homeless(
        IEnumerable<RouteEndpoint> routes, params Dictionary<string, string>[] buckets)
    {
        var keys = buckets.SelectMany(b => b.Keys).ToList();
        return routes
            .Where(e => !IsGated(e) && Longest(Route(e), keys) is null)
            .Select(e => $"{Verbs(e)} {Route(e)}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Записи корзин, под которыми есть хотя бы один незакрытый адрес.</summary>
    private static HashSet<string> Touched(
        IEnumerable<RouteEndpoint> routes, params Dictionary<string, string>[] buckets)
    {
        var keys = buckets.SelectMany(b => b.Keys).ToList();
        return routes.Where(e => !IsGated(e))
            .Select(e => Longest(Route(e), keys))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<RouteEndpoint> Routes()
    {
        _ = fixture.CreateClient();
        return fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
    }

    private static string Route(RouteEndpoint endpoint) =>
        "/" + (endpoint.RoutePattern.RawText ?? string.Empty).Trim('/');

    /// <summary>
    /// Самая длинная подходящая запись: <c>/api/notifications/health</c> обязан выиграть у
    /// <c>/api/notifications</c>, иначе исключение утонуло бы в общем правиле.
    /// </summary>
    private static string? Longest(string route, IEnumerable<string> keys) =>
        keys.Where(k => Matches(route, k)).OrderByDescending(k => k.Length).FirstOrDefault();

    /// <summary>Ворота — политика права или политика модуля. «Вошёл» и роль воротами не считаются.</summary>
    private static bool IsGated(Endpoint endpoint) =>
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a =>
            a.Policy is { } p &&
            (p.StartsWith(AppPolicies.PermissionPrefix, StringComparison.OrdinalIgnoreCase)
             || p.StartsWith(AppPolicies.ModulePrefix, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Совпадение по границе сегмента: <c>/api/plans</c> не накрывает <c>/api/plans-archive</c>.</summary>
    private static bool Matches(string route, string prefix) =>
        route.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || route.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    private static string Verbs(Endpoint endpoint) =>
        string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"]);
}
