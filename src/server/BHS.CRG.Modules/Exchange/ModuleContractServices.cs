using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Modules.Exchange;

/// <summary>
/// Что модуль зарегистрировал как реализацию контракта: читает это каталог контрактов при старте.
/// </summary>
/// <param name="Contract">Интерфейс с <see cref="ModuleContractAttribute" />.</param>
/// <param name="Implementation">Класс модуля, который его реализует.</param>
/// <param name="Module">Код модуля, чьим <c>RegisterServices</c> сделана регистрация.</param>
public sealed record ModuleContractRegistration(Type Contract, Type Implementation, string Module);

public static class ModuleContractServices
{
    /// <summary>
    /// Регистрация реализации контракта — в <c>RegisterServices</c> поставщика (ТЗ CORE-34).
    ///
    /// <para>Реализация ложится в контейнер ПО КЛЮЧУ — коду модуля, — и без ключа её не выдать. Нарочно:
    /// потребитель, попросивший интерфейс прямо (в конструкторе, <c>GetService</c>), не получит ничего
    /// даже при включённом поставщике и узнает об этом сразу, а не в тот день, когда поставщика
    /// выключат и разрешение служб упадёт. «Не подключено» умеет говорить только
    /// <see cref="IModuleContracts" />.</para>
    ///
    /// <para><paramref name="module" /> сверяется дважды: при регистрации модулей — что это код того
    /// модуля, чьими руками сделана регистрация, и каталогом при старте — что это названный у
    /// контракта поставщик.</para>
    ///
    /// <para>Время жизни — на запрос: реализация читает свою схему контекстом модуля, а он живёт
    /// столько же.</para>
    /// </summary>
    public static IServiceCollection AddModuleContract<TContract, TImplementation>(
        this IServiceCollection services, string module)
        where TContract : class
        where TImplementation : class, TContract
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);

        services.AddKeyedScoped<TContract, TImplementation>(module);
        services.AddSingleton(new ModuleContractRegistration(typeof(TContract), typeof(TImplementation), module));
        return services;
    }

    /// <summary>
    /// Регистрации контрактов, сделанные модулем, названы его кодом (зовётся сразу за его
    /// <c>RegisterServices</c>, как сверка схемы). Здесь, и только здесь, видно, ЧЬИМИ руками сделана
    /// регистрация: позже различить её и регистрацию соседа нечем, и модуль, назвавший чужой код,
    /// выглядел бы законным поставщиком.
    /// </summary>
    internal static void EnsureOwn(IAppModule module, IServiceCollection services,
        IReadOnlySet<ServiceDescriptor> before)
    {
        var foreign = services
            .Where(d => !before.Contains(d) && d.ServiceType == typeof(ModuleContractRegistration))
            .Select(d => d.ImplementationInstance)
            .OfType<ModuleContractRegistration>()
            .Where(r => !string.Equals(r.Module, module.Code, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (foreign.Count > 0)
            throw new InvalidOperationException(
                $"Модуль «{module.Code}» зарегистрировал реализацию контракта от имени другого модуля: " +
                string.Join(", ", foreign.Select(r => $"{r.Contract.Name} — как «{r.Module}»")) +
                ". Реализацию контракта регистрирует сам поставщик, своим кодом.");
    }
}
