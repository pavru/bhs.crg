using BHS.CRG.Modules;
using BHS.CRG.Modules.Exchange;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Порт <see cref="IModuleContracts" />: реализация поставщика — по ключу его кода, «не подключено» — по
/// реестру модулей (ТЗ CORE-34). Единственное место, где реализацию контракта берут из контейнера.
/// </summary>
public sealed class ModuleContractsPort(
    IServiceProvider services, ModuleContractCatalog catalog, ModuleRegistry registry) : IModuleContracts
{
    public ModuleContractAnswer<T> Get<T>() where T : class
    {
        var contract = Known<T>();
        if (contract.Many)
            throw new InvalidOperationException(
                $"Контракт {contract.Name} — со многими поставщиками: его спрашивают {nameof(GetAll)}, а не {nameof(Get)}.");

        // Сверка точная, как у каталога: реализация лежит по ключу, а ключ сравнивается с регистром.
        var provider = contract.Declaration.Provider!;
        if (!registry.Enabled.Any(m => string.Equals(m.Code, provider, StringComparison.Ordinal)))
            return new ModuleContractAnswer<T>.NotConnected(provider, TitleOf(provider), contract.Title);

        // Реализация есть наверняка: включённый поставщик без неё не дал бы приложению стартовать.
        return new ModuleContractAnswer<T>.Connected(services.GetRequiredKeyedService<T>(provider));
    }

    public IReadOnlyList<ProvidedContract<T>> GetAll<T>() where T : class
    {
        var contract = Known<T>();
        if (!contract.Many)
            throw new InvalidOperationException(
                $"Контракт {contract.Name} — одного поставщика: его спрашивают {nameof(Get)}, и выключенный " +
                $"поставщик даёт «не подключено», а не пустой список.");

        // Порядок состава поставки, а не регистраций: он один на экземпляр и не зависит от того, в каком
        // порядке модули регистрировали службы.
        return
        [
            .. registry.Enabled
                .Where(m => contract.Registrations.Any(r => string.Equals(r.Module, m.Code, StringComparison.Ordinal)))
                .Select(m => new ProvidedContract<T>(m.Code, services.GetRequiredKeyedService<T>(m.Code))),
        ];
    }

    private ModuleContract Known<T>() =>
        catalog.Find(typeof(T)) ?? throw new InvalidOperationException(
            $"{typeof(T).Name} — не межмодульный контракт: у интерфейса нет [ModuleContract], либо он лежит не " +
            "в контрактах ядра и не зарегистрирован ни одним модулем.");

    private string TitleOf(string provider) =>
        registry.Disabled.FirstOrDefault(m => string.Equals(m.Code, provider, StringComparison.Ordinal))
            ?.Title
        ?? provider;
}
