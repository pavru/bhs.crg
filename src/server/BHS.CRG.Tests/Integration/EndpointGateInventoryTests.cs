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
/// одну названную корзину: публичный, личный, открытый вошедшим, закрытый воротами внутри или
/// долг. Адрес, не попавший никуда, роняет прогон с
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
    /// под право — или в корзину «вошедшим», если разграничивать нечего. Поэтому
    /// <c>/api/notifications/health</c> вынесен отдельной строкой: он лежит среди личных
    /// уведомлений, но отвечает состоянием системы, общим для всех.
    /// </summary>
    private static readonly Dictionary<string, string> Personal = new()
    {
        ["/api/account"] = "свой профиль, свой пароль, своя почта — обработчики берут пользователя из принципала",
        ["/api/jobs"] = "свои фоновые задачи; чужая задача отвечает 404 (IJobService сверяет владельца)",
        ["/api/notifications"] = "свои уведомления и отметки о прочтении; кроме /health — он открыт всем вошедшим",
    };

    /// <summary>
    /// Открыто любому вошедшему — разграничивать нечего (решение 20.09.2026).
    ///
    /// Корзина отдельная от «личных» нарочно: там адрес отвечает данными САМОГО пользователя, а
    /// здесь — общими для всех. Смешать их значило бы потерять единственное, что делает «личную»
    /// корзину проверяемой: правило «отвечает своим — значит личный».
    ///
    /// ⚠️ Каждая запись здесь — признание, что данные видны всем вошедшим. Это не то же самое, что
    /// «неважные данные»: это решение, принятое вслух, и пересматривается оно при том же условии,
    /// что и прочие допущения о равном допуске (issue #675) — учётная запись, выданная кому-то вне
    /// компании.
    /// </summary>
    private static readonly Dictionary<string, string> SignedIn = new()
    {
        ["/api/notifications/health"] =
            "состояние системы и внешних служб показывает колокольчик, а он есть у каждого; " +
            "закрыть правом значит убрать индикатор у всех, кроме администратора",
        ["/api/system/update"] =
            "номер доступной версии показывает подвал боковой панели, то есть КАЖДЫЙ экран; " +
            "закрытая группа давала 403 на всех экранах у всех, кроме администратора (issue #813)",
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
        ["/api/bug-reports"] = "→ отправка своего обращения открыта любому вошедшему; разбор чужих "
            + "уже под правом. Остаётся решить, куда отнести саму отправку: это личное действие, "
            + "но адрес не отвечает данными пользователя, а принимает их",
    };

    /// <summary>
    /// Адреса, у которых ворота стоят ВНУТРИ, а не на самом адресе (issue #948).
    ///
    /// Корзина отдельная от долга: долг — это «прав нет», а здесь права есть и проверяются, просто
    /// не средствами маршрутизации. Смешать их значило бы потерять смысл обеих записей.
    ///
    /// ⚠️ Запись сюда обязана называть СВОЙ сторож. Иначе «ворота внутри» — это обещание, а
    /// проверять его будет некому: снаружи такой адрес неотличим от закрытого одним входом.
    /// </summary>
    private static readonly Dictionary<string, string> GatedInside = new()
    {
        ["/mcp"] = "право у каждого инструмента, ресурса и промпта MCP (ТЗ AUTH-12.1); адрес один "
            + "на все примитивы, и потребовать на нём можно только вход. Сторож — McpGateInventoryTests",
    };

    [Fact]
    public void Every_endpoint_is_gated_declared_public_or_written_into_the_debt()
    {
        var homeless = Homeless(Routes(), Public, Personal, SignedIn, GatedInside, Debt);

        Assert.True(homeless.Count == 0,
            "Адреса без ворот и без записи в списке. Каждому нужно либо право (политика perm: или " +
            "module:), либо строка в Public/Personal/SignedIn/GatedInside/Debt с объяснением:\n  " +
            string.Join("\n  ", homeless));

        var touched = Touched(Routes(), Public, Personal, SignedIn, GatedInside, Debt);
        var stale = new[] { Public, Personal, SignedIn, GatedInside, Debt }.SelectMany(b => b.Keys)
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
        var withoutDebt = Homeless(Routes(), Public, Personal, SignedIn, GatedInside);

        Assert.NotEmpty(withoutDebt);
        Assert.All(withoutDebt, line => Assert.Contains("/", line));
    }

    /// <summary>
    /// Объявленные права, которые пока не открывают ни одного адреса. Зеркало корзины долга: там
    /// адрес без права, здесь право без адреса.
    ///
    /// Зачем отдельный ратчет. Право, не стоящее ни на одной двери, невозможно заметить в работе:
    /// администратор видит галку в редакторе ролей, выдаёт её — и она не делает ничего. Выглядит
    /// это как «право выдано», а не как «права нет», и разбираются с этим не здесь и не скоро.
    /// Ровно так и вышло на ревью: <c>core.recognition.settings</c> осталось без адресов, когда
    /// ключи распознавания уехали под <c>core.system.manage</c> вместе со всей группой настроек, —
    /// и ратчет адресов об этом промолчал, потому что со стороны адресов всё было закрыто.
    /// </summary>
    private static readonly Dictionary<string, string> NotYetUsed = new()
    {
        ["core.worktypes.edit"] = "классификатор видов работ — модуль учёта работ, этап 2",
        ["core.nomenclature.edit"] = "номенклатура — модуль затрат, этап 2",
        ["core.employees.read"] = "справочник сотрудников — #962",
        ["core.employees.edit"] = "справочник сотрудников — #962",
        ["core.period.close"] = "закрытие периода — этап 2",
        ["core.views.share"] = "общие представления таблиц — отдельной группы адресов пока нет",
        ["*.read.all"] = "составное право; раскрытие по модулям — этап 2 (AUTH-5.2)",
        ["id.document.edit"] = "правка комплектов идёт теми же адресами, что и чтение; разделение "
            + "придёт с переездом кода модуля",
        ["id.quality.edit"] = "документы качества закрыты воротами МОДУЛЯ, своего права пока не носят",
    };

    /// <summary>
    /// Право без двери — такой же отказ, как дверь без права (ТЗ AUTH-8.2 — зеркально).
    /// </summary>
    [Fact]
    public void Every_declared_permission_opens_a_door_or_is_written_down_as_unused()
    {
        _ = fixture.CreateClient();
        var catalog = fixture.Services.GetRequiredService<PermissionCatalog>();

        var used = Routes()
            .SelectMany(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>())
            .Select(a => a.Policy)
            .Where(p => p is not null
                && p.StartsWith(AppPolicies.PermissionPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(p => p![AppPolicies.PermissionPrefix.Length..].Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var silent = catalog.Codes
            .Where(c => !used.Contains(c) && !NotYetUsed.ContainsKey(c))
            .Order(StringComparer.Ordinal).ToList();

        Assert.True(silent.Count == 0,
            "Права объявлены, но не стоят ни на одном адресе. Галка в редакторе ролей будет " +
            "выдаваться и не делать ничего. Поставьте право на дверь либо запишите его в " +
            "NotYetUsed с причиной:\n  " + string.Join("\n  ", silent));

        var stale = NotYetUsed.Keys.Where(c => used.Contains(c)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "Права записаны как неиспользуемые, а двери у них уже есть — уберите записи:\n  " +
            string.Join("\n  ", stale));

        var unknown = NotYetUsed.Keys.Where(c => !catalog.Declares(c)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unknown.Count == 0,
            "В списке неиспользуемых есть коды, которых нет в справочнике объявленных прав:\n  " +
            string.Join("\n  ", unknown));
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
