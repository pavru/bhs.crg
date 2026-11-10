using System.Reflection;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Exchange;

namespace BHS.CRG.Api.Modules;

/// <summary>Контракт, как его видит каталог: объявление и кто его реализовал.</summary>
public sealed record ModuleContract(
    Type Interface, ModuleContractAttribute Declaration, IReadOnlyList<ModuleContractRegistration> Registrations)
{
    public string Title => Declaration.Title;

    public bool Many => Declaration.Many;

    internal string Name => $"«{Title}» ({Interface.Name})";
}

/// <summary>
/// Межмодульные контракты экземпляра — проверенные при старте (ТЗ CORE-34; plan-22).
///
/// <para><b>Почему при старте, а не на первом вызове.</b> Контракт без реализации у включённого
/// поставщика, второй поставщик или чужая регистрация — дефект сборки поставки, а не состояние
/// данных. Найденный на первом вызове, он пришёл бы пользователю в чужой экран (заявку, «Ход работ»)
/// исключением — или, хуже, ответом «не подключено» про модуль, который включён. Поэтому каталог
/// собирается при регистрации служб и роняет запуск, называя контракт и модули.</para>
///
/// <para>Что проверяется:</para>
/// <list type="bullet">
///   <item>объявление годно (<see cref="ModuleContractAttribute.Problem" />), регистрируются только интерфейсы
///     с объявлением, регистрирует включённый модуль;</item>
///   <item>у контракта одного поставщика — не больше одной реализации, сделанной названным поставщиком, и
///     ровно одна, если поставщик включён;</item>
///   <item>у контракта со многими поставщиками — не больше одной реализации на модуль.</item>
/// </list>
///
/// <para>Поставщик, которого нет в сборке, — не отказ: контракт может появиться раньше своего
/// поставщика (интерфейс «Фактических работ» заводит этап 3, поставщика — этап 4), и до тех пор
/// потребитель получает «не подключено».</para>
/// </summary>
public sealed class ModuleContractCatalog
{
    private readonly Dictionary<Type, ModuleContract> _byInterface;

    public ModuleContractCatalog(
        ModuleRegistry registry, IEnumerable<ModuleContractRegistration> registrations, IEnumerable<Type> declared)
    {
        var all = registrations.ToList();
        var problems = new List<string>();

        foreach (var r in all)
        {
            if (!r.Contract.IsInterface || r.Contract.GetCustomAttribute<ModuleContractAttribute>() is null)
                problems.Add($"{r.Contract.Name} зарегистрирован модулем «{r.Module}» как контракт, но объявления " +
                             "[ModuleContract] у него нет");
            if (!registry.IsEnabled(r.Module))
                problems.Add($"реализацию {r.Contract.Name} зарегистрировал не включённый модуль, а «{r.Module}»: " +
                             "контракт регистрирует поставщик в своём RegisterServices");
        }

        _byInterface = declared.Concat(all.Select(r => r.Contract))
            .Where(t => t.IsInterface)
            .Distinct()
            .Select(t => (Type: t, Declaration: t.GetCustomAttribute<ModuleContractAttribute>()))
            .Where(x => x.Declaration is not null)
            .ToDictionary(x => x.Type,
                x => new ModuleContract(x.Type, x.Declaration!, [.. all.Where(r => r.Contract == x.Type)]));

        foreach (var contract in _byInterface.Values.OrderBy(c => c.Interface.FullName, StringComparer.Ordinal))
            problems.AddRange(ProblemsOf(contract, registry));

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Межмодульные контракты собраны негодно — приложение не стартует (ТЗ CORE-34):\n  " +
                string.Join("\n  ", problems));
    }

    /// <summary>Все известные контракты.</summary>
    public IReadOnlyCollection<ModuleContract> All => _byInterface.Values;

    /// <summary>Контракты со многими поставщиками — перечисляются при старте, чтобы признак не стал лазейкой.</summary>
    public IReadOnlyList<ModuleContract> ManyProviders =>
        [.. _byInterface.Values.Where(c => c.Many).OrderBy(c => c.Title, StringComparer.Ordinal)];

    public ModuleContract? Find(Type contract) => _byInterface.GetValueOrDefault(contract);

    private static IEnumerable<string> ProblemsOf(ModuleContract contract, ModuleRegistry registry)
    {
        if (contract.Declaration.Problem() is { } bad)
        {
            yield return $"контракт {contract.Name}: {bad}";
            yield break;
        }

        string Modules(IEnumerable<ModuleContractRegistration> rs) =>
            string.Join(", ", rs.Select(r => $"«{r.Module}» ({r.Implementation.Name})"));

        if (contract.Many)
        {
            foreach (var twice in contract.Registrations
                         .GroupBy(r => r.Module, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                yield return $"контракт со многими поставщиками {contract.Name}: модуль «{twice.Key}» дал " +
                             $"несколько реализаций — {Modules(twice)}; вклад модуля один";
            yield break;
        }

        var provider = contract.Declaration.Provider!;

        if (contract.Registrations.Count > 1)
            yield return $"у контракта {contract.Name} несколько реализаций: {Modules(contract.Registrations)}. " +
                         $"Поставщик у него один — «{provider}»";

        foreach (var foreign in contract.Registrations.Where(r =>
                     !string.Equals(r.Module, provider, StringComparison.OrdinalIgnoreCase)))
            yield return $"контракт {contract.Name} реализовал модуль «{foreign.Module}» ({foreign.Implementation.Name}), " +
                         $"а поставщик у контракта — «{provider}»";

        if (registry.IsEnabled(provider) && contract.Registrations.Count == 0)
            yield return $"включённый модуль «{provider}» — поставщик контракта {contract.Name} — не зарегистрировал " +
                         "его реализацию: потребитель получил бы «не подключено» при включённом поставщике";
    }

    /// <summary>
    /// Каталог и порт потребителю — в контейнер. Зовётся после <c>AddAppModules</c>: реестр модулей и
    /// регистрации контрактов к этому времени собраны.
    ///
    /// <paramref name="declared" /> — интерфейсы контрактов, известные без регистрации (чтобы увидеть
    /// включённого поставщика БЕЗ реализации). Умолчание — все объявления сборки контрактов ядра:
    /// интерфейс контракта живёт там, и только там его видят и поставщик, и потребитель.
    /// </summary>
    public static IServiceCollection AddTo(IServiceCollection services, IEnumerable<Type>? declared = null)
    {
        var registry = services
                           .Where(d => !d.IsKeyedService && d.ServiceType == typeof(ModuleRegistry))
                           .Select(d => d.ImplementationInstance).OfType<ModuleRegistry>().SingleOrDefault()
                       ?? throw new InvalidOperationException(
                           "Каталог контрактов собирается после AddAppModules: без реестра модулей не понять, " +
                           "какие поставщики включены.");

        var registrations = services
            .Where(d => !d.IsKeyedService && d.ServiceType == typeof(ModuleContractRegistration))
            .Select(d => d.ImplementationInstance).OfType<ModuleContractRegistration>();

        services.AddSingleton(new ModuleContractCatalog(registry, registrations, declared ?? DeclaredInContracts()));
        services.AddScoped<IModuleContracts, Ports.ModuleContractsPort>();
        return services;
    }

    private static IEnumerable<Type> DeclaredInContracts() =>
        typeof(ModuleContractAttribute).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.GetCustomAttribute<ModuleContractAttribute>() is not null);
}
