using System.Reflection;
using BHS.CRG.Api.Modules;
using BHS.CRG.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace BHS.CRG.Tests.Mcp;

/// <summary>Вид примитива MCP: у каждого свой атрибут SDK и своя коллекция в регистрации.</summary>
public enum McpKind { Tool, Resource, Prompt }

/// <summary>
/// Примитив MCP вместе с методом, который его объявил. Метод нужен потому, что ворота стоят атрибутом
/// НА МЕТОДЕ (<c>McpPermissionAttribute</c>): сторож проверяет именно метод, а не класс.
/// </summary>
/// <param name="NamedExplicitly">Имя задано в атрибуте. Без него имя выбирает SDK по своим правилам
/// (и не обязательно равно имени метода), так что сверка по имени соврала бы — см.
/// <see cref="McpInventory.Mismatch" />.</param>
public sealed record McpPrimitive(McpKind Kind, string Name, MethodInfo Method, Attribute Attr, bool NamedExplicitly)
{
    /// <summary>Атрибут инструмента — у ресурса и промпта его нет, вызывать только для инструмента.</summary>
    public McpServerToolAttribute Tool => (McpServerToolAttribute)Attr;

    public string KindTitle => McpInventory.Title(Kind);

    public override string ToString() => $"{Name} ({KindTitle}, {Method.DeclaringType?.FullName}.{Method.Name})";
}

/// <summary>
/// Опись примитивов MCP для сторожей (issue #1337, ТЗ AUTH-12.1). Одна на все тесты, которым нужен
/// перечень инструментов, ресурсов и промптов, — вместо списков классов, набранных в каждом руками:
/// класс, не вписанный в такой список, выпадал из проверки ворот молча.
///
/// <para>Опись собирается из двух независимых источников и сверяет их в обе стороны:</para>
/// <list type="number">
/// <item><b>Регистрация</b> (<see cref="Registered" />) — примитивы, которые сервер отдаёт агенту.
/// Читается из <see cref="McpServerOptions" /> собранного хоста: <c>ToolCollection</c>,
/// <c>ResourceCollection</c>, <c>PromptCollection</c>. Туда SDK 1.4.1 складывает всё, что положили
/// <c>WithTools&lt;T&gt;</c> / <c>WithResources&lt;T&gt;</c> / <c>WithPrompts&lt;T&gt;</c> (они
/// регистрируют синглтоны <see cref="McpServerTool" /> и др., а настройка опций собирает их в
/// коллекции). Синглтоны из контейнера напрямую не берём: коллекция опций — это то, что видит
/// транспорт, включая примитивы, добавленные в неё мимо <c>With…</c>.</item>
/// <item><b>Объявления</b> (<see cref="Declared()" />) — методы с <see cref="McpServerToolAttribute" />,
/// <see cref="McpServerResourceAttribute" />, <see cref="McpServerPromptAttribute" /> во всех типах
/// сборок <c>BHS.CRG.*</c>: API, всё, на что оно ссылается, и сборки модулей поставки
/// (<see cref="DeliveredModules" />) — даже не загруженные ещё в процесс тестов. Тестовая сборка не
/// сканируется: в ней лежит нарочное нарушение для проверки самого сторожа. Атрибут ТИПА
/// (<c>[McpServerToolType]</c>) не требуется: <c>WithTools&lt;T&gt;</c> его не смотрит, и класс без него
/// регистрируется так же.</item>
/// </list>
///
/// <para>Сопоставляются по виду и имени. Зарегистрирован, а метода нет — опись ослепла (сменилось
/// имя, SDK положил примитив иначе); метод есть, а не зарегистрирован — класс забыли подключить к
/// серверу, инструмент мёртв. Оба случая — отказ (<see cref="Mismatch" />), пустая опись — тоже.</para>
///
/// <para>⚠️ Коллекциями сервер отвечает НЕ целиком: обработчики <see cref="McpServerHandlers" />
/// дополняют выдачу мимо них — у нас <c>WithListResourcesHandler</c> (витрина объектов
/// <c>McpResourceCatalog</c>, права которой проверяет <c>McpToolGateTests</c>). Методов у обработчика
/// нет, и опись его не видит; поэтому их перечень сверяется отдельно
/// (<see cref="Handlers" />, <c>McpGateInventoryTests.Обработчики_мимо_описи_перечислены</c>).</para>
///
/// <para>Тестам без хоста (модульным) доступна только половина объявлений — <see cref="Declared()" />.
/// Её совпадение с регистрацией держит <c>McpGateInventoryTests.Опись_совпадает_с_регистрацией</c>.</para>
/// </summary>
public static class McpInventory
{
    private const string SolutionPrefix = "BHS.CRG.";

    private const BindingFlags AllMethods =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    /// <summary>Скан сборок дорог и от хоста не зависит — один на процесс тестов.</summary>
    private static readonly Lazy<IReadOnlyList<McpPrimitive>> SolutionPrimitives = new(() =>
    {
        var found = Declared(SolutionAssemblies());
        if (found.Count == 0)
            throw new InvalidOperationException(
                "Примитивов MCP в сборках решения не найдено: сторож слеп (сменились атрибуты SDK или набор сборок).");
        return found;
    });

    /// <summary>
    /// Сверенная опись собранного хоста: все объявления решения, совпавшие с регистрацией. Расхождение
    /// или пустота — исключение с перечнем: сторож, которому подали неполную опись, хуже сторожа,
    /// которого нет.
    /// </summary>
    public static IReadOnlyList<McpPrimitive> FromHost(IServiceProvider services)
    {
        var declared = Declared();
        var mismatch = Mismatch(Registered(services), declared, ExpectedUnregistered(services));
        if (mismatch.Count > 0)
            throw new InvalidOperationException(
                "Опись MCP разошлась с регистрацией сервера (WithTools/WithResources/WithPrompts):\n  "
                + string.Join("\n  ", mismatch));
        return declared;
    }

    /// <summary>Объявления в сборках решения — без тестовой. Пустота — отказ: сторож слеп.</summary>
    public static IReadOnlyList<McpPrimitive> Declared() => SolutionPrimitives.Value;

    /// <summary>Объявления в названных сборках — для проверки сторожа нарушением.</summary>
    public static IReadOnlyList<McpPrimitive> Declared(IEnumerable<Assembly> assemblies) =>
        [.. assemblies
            .Distinct()
            // GetTypes без поблажек: тип, который не загрузился, — отказ, а не пропуск, иначе он и
            // стал бы слепым пятном.
            .SelectMany(a => a.GetTypes())
            .SelectMany(t => t.GetMethods(AllMethods))
            .SelectMany(Primitive)
            .OrderBy(p => p.Kind).ThenBy(p => p.Name, StringComparer.Ordinal)];

    /// <summary>Виды и имена из регистрации сервера (способ чтения — в описании класса).</summary>
    public static IReadOnlyList<(McpKind Kind, string Name)> Registered(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        return
        [
            .. (options.ToolCollection ?? []).Select(t => (McpKind.Tool, t.ProtocolTool.Name)),
            .. (options.ResourceCollection ?? []).Select(r => (McpKind.Resource, r.ProtocolResourceTemplate.Name)),
            .. (options.PromptCollection ?? []).Select(p => (McpKind.Prompt, p.ProtocolPrompt.Name)),
        ];
    }

    /// <summary>
    /// Обработчики, заданные серверу (<see cref="McpServerHandlers" />): каждый отвечает агенту мимо
    /// коллекций, то есть мимо описи. Имена свойств, а не обработчики, — чтобы сторож мог сверить их
    /// со списком, где у каждого названо, кто его охраняет.
    /// </summary>
    public static IReadOnlyList<string> Handlers(IServiceProvider services)
    {
        var handlers = services.GetRequiredService<IOptions<McpServerOptions>>().Value.Handlers;
        return [.. typeof(McpServerHandlers).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(Delegate).IsAssignableFrom(p.PropertyType) && p.GetValue(handlers) is not null)
            .Select(p => p.Name)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Расхождение регистрации и объявлений в обе стороны — строками для сообщения отказа; пусто — совпали.
    /// Пустая регистрация — тоже расхождение: SDK, сменивший способ хранения, вернул бы пусто, и
    /// сверка «пусто с пустым» прошла бы зелёной.
    /// </summary>
    /// <param name="expectedUnregistered">Объявления, которым на этом хосте позволено не быть
    /// зарегистрированными, — примитивы модулей, выключенных на нём (<see cref="ExpectedUnregistered" />).
    /// Из описи они не выпадают: ворота у них проверяются так же.</param>
    public static List<string> Mismatch(
        IReadOnlyCollection<(McpKind Kind, string Name)> registered, IReadOnlyCollection<McpPrimitive> declared,
        Func<McpPrimitive, bool>? expectedUnregistered = null)
    {
        var lines = new List<string>();
        if (registered.Count == 0)
            lines.Add("регистрация MCP пуста: опись не видит ни одного примитива — сторож слеп");

        var declaredKeys = declared.Select(p => (p.Kind, p.Name)).ToHashSet();
        var registeredKeys = registered.ToHashSet();

        lines.AddRange(registered
            .Where(r => !declaredKeys.Contains(r))
            .Select(r => $"зарегистрирован, но метод не найден — опись ослепла: {r.Name} ({Title(r.Kind)})"));
        lines.AddRange(declared
            .Where(p => !registeredKeys.Contains((p.Kind, p.Name)) && expectedUnregistered?.Invoke(p) != true)
            .Select(p => $"объявлен, но не зарегистрирован — класс не подключён к серверу: {p}"));

        // Имя, выбранное SDK, опись угадывать не берётся: при неявном имени сверка дала бы пару ложных
        // строк выше, которые на настоящую причину не указывают. Эта — указывает.
        lines.AddRange(declared
            .Where(p => !p.NamedExplicitly)
            .Select(p => $"имя не задано в атрибуте — назовите явно (Name = …), имя часть контракта с агентом: {p}"));

        // Дубль имени: сервер отдал бы агенту один из двух, а сторож проверил бы оба.
        lines.AddRange(declared
            .GroupBy(p => (p.Kind, p.Name))
            .Where(g => g.Count() > 1)
            .Select(g => $"имя объявлено дважды: {g.Key.Name} ({Title(g.Key.Kind)})"));

        return [.. lines.Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Сборки решения: API, всё <c>BHS.CRG.*</c>, на что оно ссылается (транзитивно), и сборки модулей
    /// поставки. Берутся по ссылкам, а не из загруженных в домен: сборка модуля, которую ещё никто не
    /// тронул, иначе не попала бы в опись.
    /// </summary>
    public static IReadOnlyList<Assembly> SolutionAssemblies()
    {
        var test = typeof(McpInventory).Assembly;
        var seen = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>(
            [typeof(Program).Assembly, .. DeliveredModules.All().Select(m => m.GetType().Assembly)]);

        while (queue.TryDequeue(out var assembly))
        {
            if (assembly == test || !seen.TryAdd(assembly.GetName().Name!, assembly)) continue;
            foreach (var reference in assembly.GetReferencedAssemblies())
                if (reference.Name?.StartsWith(SolutionPrefix, StringComparison.Ordinal) == true
                    && !seen.ContainsKey(reference.Name))
                    queue.Enqueue(Assembly.Load(reference));
        }

        return [.. seen.Values.OrderBy(a => a.GetName().Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Примитив выключенного на хосте модуля: модуль вправе регистрировать свои примитивы только
    /// включённым, и «не зарегистрирован» тогда — ожидаемое, а не забытое. Сборка считается сборкой
    /// выключенного модуля, только если в ней нет ни одного включённого модуля и это не сборка API
    /// (где живёт и ядро, и модуль <c>id</c>).
    /// </summary>
    private static Func<McpPrimitive, bool> ExpectedUnregistered(IServiceProvider services)
    {
        var enabled = services.GetRequiredService<ModuleRegistry>().Enabled
            .Select(m => m.GetType().Assembly).ToHashSet();
        var off = DeliveredModules.All()
            .Select(m => m.GetType().Assembly)
            .Where(a => a != typeof(Program).Assembly && !enabled.Contains(a))
            .ToHashSet();
        return p => p.Method.DeclaringType is { } t && off.Contains(t.Assembly);
    }

    private static IEnumerable<McpPrimitive> Primitive(MethodInfo method)
    {
        if (method.GetCustomAttribute<McpServerToolAttribute>() is { } tool)
            yield return new(McpKind.Tool, tool.Name ?? method.Name, method, tool, tool.Name is not null);
        if (method.GetCustomAttribute<McpServerResourceAttribute>() is { } resource)
            yield return new(McpKind.Resource, resource.Name ?? method.Name, method, resource, resource.Name is not null);
        if (method.GetCustomAttribute<McpServerPromptAttribute>() is { } prompt)
            yield return new(McpKind.Prompt, prompt.Name ?? method.Name, method, prompt, prompt.Name is not null);
    }

    internal static string Title(McpKind kind) => kind switch
    {
        McpKind.Tool => "инструмент",
        McpKind.Resource => "ресурс",
        _ => "промпт",
    };
}
