using BHS.CRG.Modules.Exchange;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Modules.Home;

/// <summary>Очередь Главной и чья она.</summary>
public sealed record HomeQueueEntry(string Module, string ModuleTitle, ModuleHomeQueue Queue);

/// <summary>
/// Очереди Главной всей сборки (shell-08) — собираются и проверяются при старте: негодное объявление
/// обязано ронять запуск, а не первую Главную.
///
/// <para>Читаются у ВСЕХ модулей сборки, включая выключенные: перечень сверяется с клиентом
/// (<c>homeQueues.json</c>) целиком, а предлагаются очереди только включённых модулей — это решает
/// адрес Главной по реестру.</para>
/// </summary>
public sealed class HomeQueueCatalog
{
    public HomeQueueCatalog(IEnumerable<IAppModule> modules, IEnumerable<AppPermission>? corePermissions = null)
    {
        var all = modules.ToList();
        All = [.. all.SelectMany(m => m.HomeQueues.Select(q => new HomeQueueEntry(m.Code, m.Title, q)))];

        var permissions = all.SelectMany(m => m.Permissions).Concat(corePermissions ?? [])
            .Select(p => p.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var broken = new List<string>();
        foreach (var entry in All)
            broken.AddRange(entry.Queue.Problems(permissions).Select(p => $"модуль «{entry.Module}»: {p}"));
        foreach (var twice in All.GroupBy(e => (e.Module, e.Queue.Code)).Where(g => g.Count() > 1))
            broken.Add($"модуль «{twice.Key.Module}»: очередь «{twice.Key.Code}» объявлена дважды");

        if (broken.Count > 0)
            throw new InvalidOperationException(
                "Очереди Главной объявлены негодно: " + string.Join("; ", broken) + ".");
    }

    public IReadOnlyList<HomeQueueEntry> All { get; }

    /// <summary>
    /// Объявление и поставщик идут парой: зовётся сразу за <c>RegisterServices</c> ВКЛЮЧЁННОГО модуля,
    /// где видно и то и другое. Очереди без поставщика числа не получили бы никогда — карточка вечно
    /// «не посчитано»; поставщик без очередей не спрашивался бы никогда — код, который молча не работает.
    /// Общий каталог контрактов этой пары не знает: «со многими» ему всё равно, кто дал реализацию.
    /// </summary>
    public static void EnsureProvider(IAppModule module, IServiceCollection services)
    {
        var declared = module.HomeQueues.Count > 0;
        var provided = services.Any(d => d.ImplementationInstance is ModuleContractRegistration r
            && r.Contract == typeof(IHomeQueueCounts)
            && string.Equals(r.Module, module.Code, StringComparison.Ordinal));
        if (declared == provided) return;

        throw new InvalidOperationException(declared
            ? $"Модуль «{module.Code}» объявил очереди Главной, но не зарегистрировал {nameof(IHomeQueueCounts)}: " +
              "их числа не пришли бы никогда."
            : $"Модуль «{module.Code}» зарегистрировал {nameof(IHomeQueueCounts)}, но очередей Главной не объявил: " +
              "его не спросят никогда.");
    }
}
