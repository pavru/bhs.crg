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
/// разрешается при старте (<c>StartupTasks</c>) и роняет запуск, называя контракт и модули.</para>
///
/// <para><b>Почему фабрикой, а не готовым объектом при регистрации.</b> Каталог читает регистрации из
/// СОБРАННОГО контейнера: снимок коллекции служб в момент <c>AddModulePorts</c> не увидел бы реализацию,
/// зарегистрированную позже (подмена в тестовом хосте, синтетический поставщик), — она разрешалась бы по
/// ключу, а сторожа на неё не смотрели бы.</para>
///
/// <para>Что проверяется:</para>
/// <list type="bullet">
///   <item>объявление годно (<see cref="ModuleContractAttribute.Problem" />), регистрируются только интерфейсы
///     с объявлением, регистрирует включённый модуль;</item>
///   <item>коды модулей сверяются ТОЧНО, с регистром: реализация лежит в контейнере по ключу, а ключ
///     сравнивается точно, и «Plan» вместо «plan» прошёл бы старт и упал на первом вызове;</item>
///   <item>у контракта одного поставщика — не больше одной реализации, сделанной названным поставщиком, и
///     ровно одна, если поставщик включён;</item>
///   <item>у контракта со многими поставщиками — не больше одной реализации на модуль.</item>
/// </list>
///
/// <para>Поставщик, которого нет в сборке, — не отказ: контракт может появиться раньше своего
/// поставщика (интерфейс «Фактических работ» заводит этап 3, поставщика — этап 4), и до тех пор
/// потребитель получает «не подключено». Такие контракты перечислены в <see cref="ProvidersNotInBuild" />, и
/// журнал старта называет их предупреждением: опечатка в коде поставщика иначе выглядела бы как
/// выключенный модуль навсегда. Код известного модуля в другом регистре — отказ.</para>
/// </summary>
public sealed class ModuleContractCatalog
{
    private readonly Dictionary<Type, ModuleContract> _byInterface;

    public ModuleContractCatalog(
        ModuleRegistry registry, IEnumerable<ModuleContractRegistration> registrations, IEnumerable<Type> declared)
    {
        var all = registrations.ToList();
        var enabled = registry.Enabled.Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
        var delivered = registry.Enabled.Concat(registry.Disabled).Select(m => m.Code).ToList();
        var problems = new List<string>();

        foreach (var r in all)
        {
            if (!r.Contract.IsInterface || r.Contract.GetCustomAttribute<ModuleContractAttribute>() is null)
                problems.Add($"{r.Contract.Name} зарегистрирован модулем «{r.Module}» как контракт, но объявления " +
                             "[ModuleContract] у него нет");
            if (!enabled.Contains(r.Module))
                problems.Add($"реализацию {r.Contract.Name} зарегистрировал не включённый модуль, а «{r.Module}»: " +
                             "контракт регистрирует поставщик в своём RegisterServices, своим кодом — точно, с регистром");
        }

        _byInterface = declared.Concat(all.Select(r => r.Contract))
            .Where(t => t.IsInterface)
            .Distinct()
            .Select(t => (Type: t, Declaration: t.GetCustomAttribute<ModuleContractAttribute>()))
            .Where(x => x.Declaration is not null)
            .ToDictionary(x => x.Type,
                x => new ModuleContract(x.Type, x.Declaration!, [.. all.Where(r => r.Contract == x.Type)]));

        foreach (var contract in _byInterface.Values.OrderBy(c => c.Interface.FullName, StringComparer.Ordinal))
            problems.AddRange(ProblemsOf(contract, enabled, delivered));

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Межмодульные контракты собраны негодно — приложение не стартует (ТЗ CORE-34):\n  " +
                string.Join("\n  ", problems));

        ProvidersNotInBuild =
        [
            .. _byInterface.Values
                .Where(c => c.Declaration.Provider is { } p && !delivered.Contains(p, StringComparer.Ordinal))
                .OrderBy(c => c.Title, StringComparer.Ordinal),
        ];
    }

    /// <summary>Контракты со многими поставщиками — перечисляются при старте, чтобы признак не стал лазейкой.</summary>
    public IReadOnlyList<ModuleContract> ManyProviders =>
        [.. _byInterface.Values.Where(c => c.Many).OrderBy(c => c.Title, StringComparer.Ordinal)];

    /// <summary>
    /// Контракты, чьего поставщика нет в этой сборке: законно (поставщик придёт позже), но называется
    /// предупреждением при старте — иначе опечатку в коде поставщика не отличить от выключенного модуля.
    /// </summary>
    public IReadOnlyList<ModuleContract> ProvidersNotInBuild { get; }

    public ModuleContract? Find(Type contract) => _byInterface.GetValueOrDefault(contract);

    private static IEnumerable<string> ProblemsOf(
        ModuleContract contract, IReadOnlySet<string> enabled, IReadOnlyList<string> delivered)
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
                         .GroupBy(r => r.Module, StringComparer.Ordinal).Where(g => g.Count() > 1))
                yield return $"контракт со многими поставщиками {contract.Name}: модуль «{twice.Key}» дал " +
                             $"несколько реализаций — {Modules(twice)}; вклад модуля один";
            yield break;
        }

        var provider = contract.Declaration.Provider!;

        // Код известного модуля в другом регистре — не «поставщика нет в сборке», а опечатка: реализация
        // лежала бы под одним ключом, а спрашивали бы другой.
        if (!delivered.Contains(provider, StringComparer.Ordinal)
            && delivered.FirstOrDefault(c => string.Equals(c, provider, StringComparison.OrdinalIgnoreCase)) is { } real)
            yield return $"контракт {contract.Name} называет поставщика «{provider}», а модуль называется «{real}»: " +
                         "код сверяется точно, с регистром";

        if (contract.Registrations.Count > 1)
            yield return $"у контракта {contract.Name} несколько реализаций: {Modules(contract.Registrations)}. " +
                         $"Поставщик у него один — «{provider}»";

        foreach (var foreign in contract.Registrations.Where(r => !string.Equals(r.Module, provider, StringComparison.Ordinal)))
            yield return $"контракт {contract.Name} реализовал модуль «{foreign.Module}» ({foreign.Implementation.Name}), " +
                         $"а поставщик у контракта — «{provider}»";

        if (enabled.Contains(provider) && contract.Registrations.Count == 0)
            yield return $"включённый модуль «{provider}» — поставщик контракта {contract.Name} — не зарегистрировал " +
                         "его реализацию: потребитель получил бы «не подключено» при включённом поставщике";
    }

    /// <summary>
    /// Каталог и порт потребителю — в контейнер. Каталог строится фабрикой из собранного контейнера (реестр
    /// модулей и все регистрации контрактов) и ОБЯЗАН быть разрешён при старте — это делает
    /// <c>StartupTasks</c>, как с остальными каталогами.
    ///
    /// <paramref name="declared" /> — интерфейсы контрактов, известные без регистрации (чтобы увидеть
    /// включённого поставщика БЕЗ реализации). Умолчание — все объявления сборки контрактов ядра:
    /// интерфейс контракта живёт там, и только там его видят и поставщик, и потребитель.
    /// </summary>
    public static IServiceCollection AddTo(IServiceCollection services, IEnumerable<Type>? declared = null)
    {
        var known = (declared ?? DeclaredInContracts()).ToList();
        services.AddSingleton(sp => new ModuleContractCatalog(
            sp.GetRequiredService<ModuleRegistry>(), sp.GetServices<ModuleContractRegistration>(), known));
        services.AddScoped<IModuleContracts, Ports.ModuleContractsPort>();
        return services;
    }

    private static IEnumerable<Type> DeclaredInContracts() =>
        typeof(ModuleContractAttribute).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.GetCustomAttribute<ModuleContractAttribute>() is not null);
}
