using System.Text.RegularExpressions;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Tests.Common;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Реализацию контракта берут ТОЛЬКО портом <see cref="IModuleContracts" /> (ТЗ CORE-34; plan-22).
///
/// <para>Реализация лежит в контейнере по ключу — коду поставщика, — поэтому конструктор, требующий
/// интерфейс, не разрешается уже при включённом поставщике, и это видно сразу. Опасны две другие группы.
/// Запрос С КЛЮЧОМ (<c>GetRequiredKeyedService&lt;T&gt;("plan")</c>, <c>[FromKeyedServices("plan")]</c>)
/// работает, пока поставщик включён, и падает разрешением служб в день, когда его выключат, — вместо
/// «не подключено». ТИХИЕ формы — <c>GetService&lt;T&gt;</c>, <c>GetServices&lt;T&gt;</c>,
/// <c>IEnumerable&lt;T&gt;</c> в параметре, необязательный <c>T? x = null</c> — отдают <c>null</c> или
/// пустой список без ошибки, и потребитель рисует «данных нет». Всё это перепись и ловит — по исходникам
/// ядра и модулей.</para>
/// </summary>
public class ModuleContractRequestInventoryTests
{
    private static readonly string[] Projects =
        SolutionModules.WithCore("BHS.CRG.Api", "BHS.CRG.Application", "BHS.CRG.Infrastructure");

    /// <summary>Как интерфейс контракта просят у контейнера мимо порта.</summary>
    private static IEnumerable<Regex> Requests(string contract)
    {
        var n = Regex.Escape(contract);
        yield return new($@"\bGet(Required)?(Keyed)?Services?\s*<\s*{n}\s*>");
        yield return new($@"\bGet(Required)?(Keyed)?Services?\s*\(\s*typeof\s*\(\s*{n}\s*\)");
        yield return new($@"\[\s*From(Keyed)?Services\b[^\]]*\]\s*{n}\b");
        // Перечисление по ключу не видит вовсе: в параметре или в запросе — всегда пустой список.
        yield return new($@"\bIEnumerable\s*<\s*{n}\s*>");
        // Необязательная зависимость: контейнер подставит null и не скажет ни слова.
        yield return new($@"\b{n}\s*\?\s+\w+\s*=\s*null\b");
    }

    /// <summary>Строки исходника, где контракт просят у контейнера прямо.</summary>
    public static IReadOnlyList<int> DirectRequests(string source, IReadOnlyCollection<string> contracts)
    {
        var patterns = contracts.SelectMany(Requests).ToList();
        var lines = source.Split('\n');
        var found = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (line.StartsWith("//")) continue;
            if (patterns.Any(p => p.IsMatch(line))) found.Add(i + 1);
        }
        return found;
    }

    private static IReadOnlyCollection<string> DeclaredContracts() =>
    [
        .. typeof(ModuleContractAttribute).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.IsDefined(typeof(ModuleContractAttribute), false))
            .Select(t => t.Name),
    ];

    [Fact]
    public void Контракт_берут_только_портом()
    {
        var contracts = DeclaredContracts();
        var outsiders = Projects.SelectMany(SourceTree.Files)
            .SelectMany(file => DirectRequests(File.ReadAllText(file), contracts)
                .Select(line => $"{SourceTree.Relative(file)}:{line}"))
            .ToList();

        Assert.True(outsiders.Count == 0,
            "Интерфейс межмодульного контракта попросили у контейнера мимо IModuleContracts:\n  " +
            string.Join("\n  ", outsiders) + "\n\n" +
            "У выключенного поставщика такой запрос — исключение разрешения служб (или пустой список), а не " +
            "«не подключено». Берите контракт портом: IModuleContracts.Get<T>() / GetAll<T>().");
    }

    [Fact]
    public void Образец_прямого_запроса_находится()
    {
        // Сторож сегодня смотрит на пустой список контрактов — настоящие приносят plan-23…plan-25, — и без
        // этой проверки он был бы зелёным просто потому, что искать нечего.
        const string sample = """
            var a = sp.GetRequiredKeyedService<IProbePlan>("supplier");
            var b = sp.GetService<IProbePlan>();
            var c = sp.GetServices<IProbePlan>();
            var d = sp.GetRequiredService(typeof(IProbePlan));
            app.MapGet("/x", ([FromKeyedServices("supplier")] IProbePlan plan) => plan.Ask());
            public Consumer([FromServices] IProbePlan plan) { }
            // var e = sp.GetService<IProbePlan>();  — комментарий не в счёт
            var f = contracts.Get<IProbePlan>();
            services.AddModuleContract<IProbePlan, ProbePlan>(code);
            public sealed class ProbePlan : IProbePlan { }
            var g = sp.GetService<IProbePlanner>();
            public Consumer(IEnumerable<IProbePlan> plans) { }
            var h = sp.GetRequiredService<IEnumerable<IProbePlan>>();
            public Consumer(IProbePlan? plan = null) { }
            IReadOnlyList<ProvidedContract<IProbePlan>> all = contracts.GetAll<IProbePlan>();
            """;

        Assert.Equal([1, 2, 3, 4, 5, 6, 12, 13, 14], DirectRequests(sample, [nameof(IProbePlan)]));
    }
}
