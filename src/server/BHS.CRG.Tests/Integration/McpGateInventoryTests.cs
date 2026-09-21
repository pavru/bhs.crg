using System.Reflection;
using BHS.CRG.Api.Mcp;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

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
/// </summary>
[Collection("Integration")]
public class McpGateInventoryTests(IntegrationTestFixture fixture)
{
    private static readonly Type[] ToolTypes =
    [
        typeof(DataSnapshotTools), typeof(DomainSnapshotTools), typeof(DocumentActionTools),
        typeof(ObservationTools), typeof(ReconciliationTools), typeof(JobTools), typeof(OperationTools),
    ];

    private static readonly Type[] ResourceTypes =
        [typeof(DataSnapshotResources), typeof(DomainSnapshotResources)];

    private static readonly Type[] PromptTypes = [typeof(ReconciliationPrompts)];

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

    [Fact]
    public void Каждый_инструмент_объявляет_право_модуль_или_записан_как_личный()
    {
        var homeless = Homeless(Personal);

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
        var withoutPersonal = Homeless([]);

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
        _ = fixture.CreateClient();
        var catalog = fixture.Services.GetRequiredService<PermissionCatalog>();
        var modules = fixture.Services.GetRequiredService<ModuleRegistry>();

        var unknownPermissions = Primitives()
            .Select(p => (p.Name, Attr: p.Method.GetCustomAttribute<McpPermissionAttribute>(false)))
            .Where(x => x.Attr is not null && !catalog.Declares(x.Attr!.Code))
            .Select(x => $"{x.Name} → {x.Attr!.Code}")
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

    private static List<string> Homeless(Dictionary<string, string> personal) =>
        [.. Primitives()
            .Where(p => Gate(p.Method) is null && !personal.ContainsKey(p.Name))
            .Select(p => $"{p.Name} ({p.Kind})")
            .Order(StringComparer.Ordinal)];

    /// <summary>Ворота НА САМОМ МЕТОДЕ: атрибут класса SDK учёл бы, но тогда новый инструмент
    /// молча получал бы соседские ворота — см. <see cref="McpPermissionAttribute" />.</summary>
    private static AuthorizeAttribute? Gate(MethodInfo method) =>
        (AuthorizeAttribute?)method.GetCustomAttribute<McpPermissionAttribute>(false)
        ?? method.GetCustomAttribute<McpModuleAttribute>(false);

    private static IEnumerable<(string Name, string Kind, MethodInfo Method)> Primitives()
    {
        foreach (var x in Declared<McpServerToolAttribute>(ToolTypes))
            yield return (x.Attr.Name ?? x.Method.Name, "инструмент", x.Method);
        foreach (var x in Declared<McpServerResourceAttribute>(ResourceTypes))
            yield return (x.Attr.Name ?? x.Method.Name, "ресурс", x.Method);
        foreach (var x in Declared<McpServerPromptAttribute>(PromptTypes))
            yield return (x.Attr.Name ?? x.Method.Name, "промпт", x.Method);
    }

    private static IEnumerable<(MethodInfo Method, TAttr Attr)> Declared<TAttr>(Type[] types)
        where TAttr : Attribute
        => types
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<TAttr>()))
            .Where(x => x.Attr is not null)
            .Select(x => (x.Method, x.Attr!));
}
