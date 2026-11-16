using System.Reflection;
using BHS.CRG.Api.Mcp;
using BHS.CRG.Modules;
using BHS.CRG.Tests.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Инвентаризация инструментов MCP (issue #948, ТЗ AUTH-12.1) — та же, что у адресов
/// (<see cref="EndpointGateInventoryTests" />), и по той же причине.
///
/// Адрес <c>/mcp</c> один на все примитивы, и потребовать на нём можно только «пользователь вошёл».
/// Значит, ворота стоят внутри, у каждого инструмента, ресурса и промпта, — а такие ворота легко не
/// поставить: инструмент без них работает, отвечает данными и выглядит исправным. Заметить это
/// можно только перечислением, и перечисляет машина.
///
/// ⚠️ Список <see cref="Personal" /> — ратчет, а не механизм: в работе приложения его не читает
/// никто. Поэтому запись, потерявшая свой инструмент, — тоже отказ: список, разошедшийся с кодом,
/// перестаёт что-либо утверждать, продолжая выглядеть утверждением.
///
/// <para>Перечень примитивов — из регистрации сервера, сверенной с методами в сборках решения
/// (<see cref="McpInventory" />, issue #1337), а не из списка классов: класс, не вписанный в такой
/// список, — в том числе класс модуля в своей сборке, — выпадал из проверки молча.</para>
/// </summary>
[Collection("Integration")]
public class McpGateInventoryTests(IntegrationTestFixture fixture)
{
    private IReadOnlyList<McpPrimitive>? _primitives;

    /// <summary>
    /// Примитивы без права: отвечают данными САМОГО спрашивающего, разграничивать нечего. Та же
    /// корзина, что «личные адреса» у адресной инвентаризации, и граница у неё такая же жёсткая:
    /// сюда пускают только то, что видно по коду обработчика.
    /// </summary>
    private static readonly Dictionary<string, string> Personal = new()
    {
        ["get_job"] = "свои фоновые задачи; чужая задача отвечает отказом «не найдена» — IJobService "
            + "сверяет владельца, как и на /api/jobs",
    };

    /// <summary>
    /// Опись = регистрация, в обе стороны (issue #1337): зарегистрированный примитив без найденного
    /// метода значит, что опись ослепла, а метод без регистрации — что класс забыли подключить к
    /// серверу. Пустая регистрация — тоже отказ: сверка «пусто с пустым» прошла бы зелёной.
    /// </summary>
    [Fact]
    public void Опись_совпадает_с_регистрацией()
    {
        var registered = McpInventory.Registered(Services());
        var mismatch = McpInventory.Mismatch(registered, McpInventory.Declared());

        Assert.True(mismatch.Count == 0,
            "Опись MCP разошлась с регистрацией сервера (WithTools/WithResources/WithPrompts):\n  "
            + string.Join("\n  ", mismatch));
        Assert.Contains(registered, r => r.Kind == McpKind.Tool);
    }

    /// <summary>
    /// Сторож проверен нарушением (issue #1337): класс инструментов без ворот, нигде не вписанный и не
    /// зарегистрированный (<see cref="UnlistedMcpTools" />), не проходит молча — опись называет его
    /// инструмент и как незарегистрированный, и как бездомный. Основная опись тестовую сборку не
    /// видит, поэтому сам класс остальным сторожам не мешает.
    /// </summary>
    [Fact]
    public void Невписанный_класс_не_проходит_молча()
    {
        Assert.DoesNotContain(McpInventory.Declared(), p => p.Name == UnlistedMcpTools.Name);

        var declared = McpInventory.Declared(
            [.. McpInventory.SolutionAssemblies(), typeof(UnlistedMcpTools).Assembly]);
        var mismatch = McpInventory.Mismatch(McpInventory.Registered(Services()), declared);

        Assert.Contains(mismatch, line => line.Contains(UnlistedMcpTools.Name, StringComparison.Ordinal));
        Assert.Contains(Homeless(declared, Personal), line => line.StartsWith(UnlistedMcpTools.Name + " ", StringComparison.Ordinal));
    }

    [Fact]
    public void Каждый_инструмент_объявляет_право_модуль_или_записан_как_личный()
    {
        var homeless = Homeless(Primitives(), Personal);

        Assert.True(homeless.Count == 0,
            "Примитивы MCP без ворот. Каждому нужен [McpPermission] либо [McpModule] НА МЕТОДЕ — или "
            + "строка в Personal с объяснением, почему разграничивать нечего:\n  "
            + string.Join("\n  ", homeless));

        var named = Primitives().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var stale = Personal.Keys.Where(k => !named.Contains(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "Записи, под которыми больше нет инструмента. Список разошёлся с кодом — уберите их:\n  "
            + string.Join("\n  ", stale));

        var gated = Primitives().Where(p => Gate(p.Method) is not null).Select(p => p.Name);
        var both = Personal.Keys.Intersect(gated, StringComparer.Ordinal).Order().ToList();
        Assert.True(both.Count == 0,
            "Инструмент записан как личный и при этом закрыт правом. Одно из двух неверно:\n  "
            + string.Join("\n  ", both));
    }

    /// <summary>
    /// Проверка проверяется нарушением: убираем корзину личных — и сторож обязан заговорить.
    /// Без этого «зелено» означало бы лишь то, что сторож ничего не смотрит.
    /// </summary>
    [Fact]
    public void Сторож_говорит_когда_ворот_нет()
    {
        var withoutPersonal = Homeless(Primitives(), []);

        Assert.NotEmpty(withoutPersonal);
        Assert.All(withoutPersonal, line => Assert.Contains("(", line));
    }

    /// <summary>
    /// Право в воротах обязано быть ОБЪЯВЛЕННЫМ, а модуль — включённым. Ворота на необъявленное
    /// право не откроются никому и будут выглядеть как правильная работа прав: отказ у них такой же,
    /// как у настоящих. У адресов эту же ошибку ловит <see cref="AppPolicyProvider" /> при сборке
    /// политики — то есть на первом запросе; здесь дешевле поймать раньше.
    /// </summary>
    [Fact]
    public void Ворота_ссылаются_только_на_объявленные_права_и_включённые_модули()
    {
        var catalog = Services().GetRequiredService<PermissionCatalog>();
        var modules = Services().GetRequiredService<ModuleRegistry>();

        var unknownPermissions = Primitives()
            .Select(p => (p.Name, Attr: p.Method.GetCustomAttribute<McpPermissionAttribute>(false)))
            .SelectMany(x => x.Attr?.Codes.Select(code => (x.Name, Code: code)) ?? [])
            .Where(x => !catalog.Declares(x.Code))
            .Select(x => $"{x.Name} → {x.Code}")
            .Order(StringComparer.Ordinal).ToList();

        Assert.True(unknownPermissions.Count == 0,
            "Ворота требуют право, которого нет в справочнике объявленных прав:\n  "
            + string.Join("\n  ", unknownPermissions));

        var unknownModules = Primitives()
            .Select(p => (p.Name, Attr: p.Method.GetCustomAttribute<McpModuleAttribute>(false)))
            .Where(x => x.Attr is not null
                && !modules.Enabled.Any(m => string.Equals(m.Code, x.Attr!.Code, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.Name} → {x.Attr!.Code}")
            .Order(StringComparer.Ordinal).ToList();

        Assert.True(unknownModules.Count == 0,
            "Ворота требуют модуль, которого нет среди включённых:\n  "
            + string.Join("\n  ", unknownModules));
    }

    /// <summary>
    /// Голый <c>[Authorize]</c> означает «достаточно войти» — то есть ворота, которых в этой
    /// инвентаризации не видно: примитив выглядит закрытым и открыт любому вошедшему.
    /// </summary>
    [Fact]
    public void Ворота_ставятся_только_нашими_атрибутами()
    {
        var bare = Primitives()
            .Where(p => p.Method.GetCustomAttributes<AuthorizeAttribute>(false)
                .Any(a => a is not (McpPermissionAttribute or McpModuleAttribute)))
            .Select(p => p.Name).Order(StringComparer.Ordinal).ToList();

        Assert.True(bare.Count == 0,
            "У примитива стоит голый [Authorize]: он требует лишь входа, а выглядит как право:\n  "
            + string.Join("\n  ", bare));
    }

    /// <summary>
    /// Изменяющий инструмент не достаётся владельцу одного «читать всё» (задача A3, issue #1074;
    /// ревью PR #1251). То же правило, что у адресов (<c>ReadAllWritesNothingTests</c>): после
    /// раскрытия составного права ворота МОДУЛЯ проходит и тот, у кого в модуле одно чтение, — а
    /// инструменты стоят под теми же политиками, и сторож адресов их не видит.
    ///
    /// <para>Считается по всем модулям поставки, а не по включённым на тестовом хосте: инструмент
    /// под воротами выключенного модуля иначе не проверялся бы вовсе.</para>
    /// </summary>
    [Fact]
    public void Изменяющий_инструмент_не_открывается_одним_правом_читать_всё()
    {
        var catalog = new PermissionCatalog(
        [
            .. BHS.CRG.Api.Auth.CorePermissions.All,
            .. BHS.CRG.Api.Modules.DeliveredModules.All().SelectMany(m => m.Permissions),
        ]);
        var granted = catalog.Expand([PermissionCatalog.ReadAllCode]);
        Assert.True(granted.Count > 1, "В «читать всё» не входит ни одно право: проверять нечем.");

        var writers = Primitives().Where(p => p.Kind == McpKind.Tool && !p.Tool.ReadOnly).ToList();
        // Изменяющие инструменты есть — иначе признак ReadOnly читался бы не так, и сторож молчал бы.
        Assert.NotEmpty(writers);

        var open = writers
            .Where(w => Gate(w.Method) switch
            {
                McpPermissionAttribute p => p.Codes.Any(granted.Contains),
                McpModuleAttribute m => ModuleAccess.IsOpen(m.Code, granted, catalog),
                _ => false,   // без ворот — «личный» инструмент, у него свой сторож выше
            })
            .Select(w => w.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(open.Count == 0,
            "Изменяющий инструмент MCP открыт владельцу одного составного права «читать всё»:\n  "
            + string.Join("\n  ", open) + "\n\n"
            + "Ворота модуля проходит и тот, у кого в модуле одно чтение, а право с пометкой "
            + "ReadAllMark.In приходит раскрытием. Поставьте инструменту право на правку.");
    }

    private static List<string> Homeless(
        IEnumerable<McpPrimitive> primitives, Dictionary<string, string> personal) =>
        [.. primitives
            .Where(p => Gate(p.Method) is null && !personal.ContainsKey(p.Name))
            .Select(p => $"{p.Name} ({p.KindTitle})")
            .Order(StringComparer.Ordinal)];

    /// <summary>Ворота НА САМОМ МЕТОДЕ: атрибут класса SDK учёл бы, но тогда новый инструмент
    /// молча получал бы соседские ворота — см. <see cref="McpPermissionAttribute" />.</summary>
    private static AuthorizeAttribute? Gate(MethodInfo method) =>
        (AuthorizeAttribute?)method.GetCustomAttribute<McpPermissionAttribute>(false)
        ?? method.GetCustomAttribute<McpModuleAttribute>(false);

    /// <summary>Сверенная опись собранного хоста; расхождение с регистрацией — отказ здесь же.</summary>
    private IReadOnlyList<McpPrimitive> Primitives() => _primitives ??= McpInventory.FromHost(Services());

    /// <summary>Контейнер хоста собирается при первом клиенте — до него <c>Services</c> пуст.</summary>
    private IServiceProvider Services()
    {
        _ = fixture.CreateClient();
        return fixture.Services;
    }
}
