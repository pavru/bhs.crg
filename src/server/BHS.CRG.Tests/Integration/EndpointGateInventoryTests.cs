using BHS.CRG.Modules;
using BHS.CRG.Tests.Support;
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
        ["/api/branding"] = "название и логотип экземпляра стоят на СТРАНИЦЕ ВХОДА (ТЗ CORE-25.1), "
            + "то есть обязаны читаться до входа; правка — под core.system.manage. Кто дошёл до "
            + "страницы входа, тот видит, чей это экземпляр: другого прочтения у «покажите наш "
            + "логотип на входе» нет. ⚠️ Запись по ПРЕФИКСУ, как и остальные: следующий адрес под "
            + "/api/branding сторож пропустит молча — это цена корзин по группам, и заводить такой "
            + "адрес нужно с тем же решением вслух (ревью PR #1061)",
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
        ["/api/account"] = "свой профиль, свой пароль, своя почта, свои настройки (тема и язык, "
            + "issue #953) — обработчики берут пользователя из принципала",
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
        ["/api/files/kinds"] =
            "реестр видов файлов: что система умеет показать и распознать. Нужен раньше любого права — " +
            "из него собирается выбор файла, а данных в ответе нет",
        ["/api/notifications/health"] =
            "состояние системы и внешних служб показывает колокольчик, а он есть у каждого; " +
            "закрыть правом значит убрать индикатор у всех, кроме администратора",
        ["/api/periods"] =
            "границы закрытия периода — даты, а не суммы: по ним экран записи объясняет, почему она " +
            "не правится и в какой месяц ляжет оплата. Закрытие и отмена — под core.period.close",
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
        ["/api/tables"] = "ключ у каждой таблицы свой — его называет объявление модуля (ТЗ CORE-33, "
            + "G1b); группа общая на таблицы всех модулей, и потребовать на ней можно только вход. "
            + "Сторож — ModuleTableTests.Без_модуля_таблица_отказывает",
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
    /// Единственное исключение инвентаризации — отказы выключенных модулей — проверяется по существу:
    /// помеченный адрес обязан принадлежать ВЫКЛЮЧЕННОМУ модулю, лежать под его объявленным путём и
    /// быть анонимным.
    ///
    /// Иначе исключение стало бы дырой в сторожe: пометка на обычном адресе выносила бы его из
    /// инвентаризации молча — ровно та поломка, против которой инвентаризация и написана.
    ///
    /// ⚠️ Сегодня проверка идёт на живых данных: в сборке есть выключенный модуль (`costs` включается
    /// только настройкой поставки). Станет их ноль — проверять исключение будет не на чем, и это
    /// повод перенести её туда, где выключенный модуль есть, а не считать её зелёной.
    /// </summary>
    [Fact]
    public void Refusal_endpoints_are_refusals_of_disabled_modules()
    {
        _ = fixture.CreateClient();
        var registry = fixture.Services.GetRequiredService<ModuleRegistry>();
        var disabled = registry.Disabled.ToDictionary(m => m.Code, m => m.RoutePrefixes);

        var marked = EndpointInventory.Routes(fixture.Services, includeRefusals: true)
            .Select(e => (Endpoint: e, Mark: e.Metadata.GetMetadata<DisabledModuleEndpoint>()))
            .Where(x => x.Mark is not null)
            .ToList();

        // Исключение, которое не на чем проверить, — это не исключение, а выход из инвентаризации:
        // пометка, поставленная на обычный незакрытый адрес, унесла бы его отсюда молча. Поэтому
        // пустой список — отказ, а не «нечего проверять» (приём соседнего теста
        // Guard_speaks_when_a_gate_is_missing). Если выключенных модулей в сборке не осталось,
        // переносите проверку туда, где они есть, — а не считайте её зелёной (ревью PR #1105).
        Assert.True(marked.Count > 0,
            "В сборке нет ни одного адреса-отказа, то есть нет выключенных модулей: единственное " +
            "исключение инвентаризации проверять не на чем.");

        var offenders = new List<string>();
        foreach (var (endpoint, mark) in marked)
        {
            var route = Route(endpoint);

            if (!disabled.TryGetValue(mark!.Code, out var prefixes))
                offenders.Add($"{route} — помечен отказом модуля «{mark.Code}», а тот не выключен");
            else if (!prefixes.Any(prefix => Matches(route, prefix)))
                offenders.Add($"{route} — вне объявленных путей модуля «{mark.Code}»");

            if (endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0)
                offenders.Add($"{route} — помечен отказом, но требует авторизации");

            // Именно наличие IAllowAnonymous, а не отсутствие ворот: анонимность здесь обещана
            // вслух, а снимается она СВОИМ способом — пропажей .AllowAnonymous(), которую проверка
            // «ворот нет» не заметила бы (ревью PR #1105). Без неё отказ начал бы требовать вход,
            // то есть прятать состав поставки от того, кто и так увидит его в интерфейсе.
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
                offenders.Add($"{route} — помечен отказом, но не объявлен анонимным");
        }

        Assert.True(offenders.Count == 0,
            "Пометка отказа стоит там, где её быть не должно — такой адрес выпал бы из " +
            "инвентаризации молча:\n  " + string.Join("\n  ", offenders));
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
        ["core.views.share"] = "общие представления таблиц — отдельной группы адресов пока нет",
        ["*.read.all"] = "составное право: на дверях стоят права, в которые оно раскрывается "
            + "(AUTH-5.2, issue #1074), — само оно воротами не бывает",
    };

    /// <summary>
    /// Право без двери — такой же отказ, как дверь без права (ТЗ AUTH-8.2 — зеркально).
    /// </summary>
    [Fact]
    public void Every_declared_permission_opens_a_door_or_is_written_down_as_unused()
    {
        _ = fixture.CreateClient();
        var catalog = fixture.Services.GetRequiredService<PermissionCatalog>();

        var used = EndpointInventory.GatingPermissions(Routes());

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

    /// <summary>
    /// Адреса живого приложения — кроме отказов выключенных модулей (см.
    /// <see cref="EndpointInventory.Routes" />: правило и причина исключения живут там, потому что
    /// инвентаризовать приходится два хоста с разным составом модулей).
    /// </summary>
    private IEnumerable<RouteEndpoint> Routes()
    {
        _ = fixture.CreateClient();
        return EndpointInventory.Routes(fixture.Services);
    }

    /// <summary>
    /// Самая длинная подходящая запись: <c>/api/notifications/health</c> обязан выиграть у
    /// <c>/api/notifications</c>, иначе исключение утонуло бы в общем правиле.
    /// </summary>
    private static string? Longest(string route, IEnumerable<string> keys) =>
        keys.Where(k => Matches(route, k)).OrderByDescending(k => k.Length).FirstOrDefault();

    private static string Route(RouteEndpoint endpoint) => EndpointInventory.Route(endpoint);

    private static bool IsGated(Endpoint endpoint) => EndpointInventory.IsGated(endpoint);

    private static bool Matches(string route, string prefix) => EndpointInventory.Matches(route, prefix);

    private static string Verbs(Endpoint endpoint) => EndpointInventory.Verbs(endpoint);
}
