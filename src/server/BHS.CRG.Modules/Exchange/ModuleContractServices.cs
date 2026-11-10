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
    /// конструктор, требующий интерфейс, не разрешится и при включённом поставщике — ошибка видна сразу, а
    /// не в тот день, когда поставщика выключат. ⚠️ Но тихие формы — <c>IEnumerable&lt;T&gt;</c> (пустой
    /// список), необязательный <c>T? x = null</c>, <c>GetService&lt;T&gt;</c> (<c>null</c>) — отдают
    /// пустоту без ошибки; их и запросы с ключом ловит перепись <c>ModuleContractRequestInventoryTests</c>.
    /// «Не подключено» умеет говорить только <see cref="IModuleContracts" />.</para>
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
            // Точно, с регистром: реализация лежит по ключу, а ключ сравнивается точно — «Costs» вместо
            // «costs» прошёл бы здесь и не нашёлся бы при вызове.
            .Where(r => !string.Equals(r.Module, module.Code, StringComparison.Ordinal))
            .ToList();

        if (foreign.Count > 0)
            throw new InvalidOperationException(
                $"Модуль «{module.Code}» зарегистрировал реализацию контракта от имени другого модуля: " +
                string.Join(", ", foreign.Select(r => $"{r.Contract.Name} — как «{r.Module}»")) +
                ". Реализацию контракта регистрирует сам поставщик, своим кодом.");
    }
}
