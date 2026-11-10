using BHS.CRG.Api.Modules;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Configuration;

// Пробные контракты и модули сторожей контракта (plan-22). Настоящих контрактов change не заводит — их
// приносят plan-23…plan-25, — поэтому механизм проверяется здесь, на модулях, которых нет в поставке.
// Интерфейсы публичные: атрибут читается отражением, а каталог получает их списком объявленных.

[ModuleContract(ContractProbes.Supplier, "Пробный план")]
public interface IProbePlan
{
    string Ask();
}

[ModuleContract("Пробный вклад", Many = true)]
public interface IProbeContribution
{
    string From { get; }
}

[ModuleContract(ContractProbes.Supplier, "Пробные объёмы")]
public interface IProbeVolumes
{
    ContractAnswer<int> Read(ModuleReader reader);
}

/// <summary>Объявлен негодно: поставщика нет, а «со многими» не сказано.</summary>
[ModuleContract("Без поставщика")]
public interface IProbeUnnamed;

/// <summary>Не контракт: объявления нет.</summary>
public interface IProbeNotContract;

public sealed class ProbePlan : IProbePlan, IProbeNotContract
{
    public string Ask() => "ответ поставщика";
}

public sealed class ProbeOtherPlan : IProbePlan
{
    public string Ask() => "ответ второго";
}

public sealed class ProbeUnnamed : IProbeUnnamed;

public sealed class SupplierContribution : IProbeContribution
{
    public string From => ContractProbes.Supplier;
}

public sealed class ConsumerContribution : IProbeContribution
{
    public string From => ContractProbes.Consumer;
}

public sealed class ProbeVolumes : IProbeVolumes
{
    public const string ReadPermission = "supplier.volumes.read";
    public const string Narrow = "supplier.volumes.hint";

    public ContractAnswer<int> Read(ModuleReader reader) =>
        ContractAnswer.Refusal<int>(reader, ReadPermission, Narrow) ?? ContractAnswer.Shown(42);
}

public sealed class ProbeModule(string code, string title, Action<IServiceCollection, string>? register = null)
    : IAppModule
{
    public string Code => code;
    public string Title => title;
    public IReadOnlyList<AppPermission> Permissions => [];
    public IReadOnlyList<string> RoutePrefixes => ["/api/" + code];
    public void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        register?.Invoke(services, code);
    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
    public Task InitializeAsync(IServiceProvider services, CancellationToken ct) => Task.CompletedTask;
}

public static class ContractProbes
{
    public const string Supplier = "supplier";
    public const string Consumer = "consumer";
    public const string SupplierTitle = "Поставщик";

    /// <summary>
    /// Поставка из модулей <paramref name="modules" /> с включёнными <paramref name="enabled" /> — как её
    /// собирает приложение: модули, затем каталог контрактов. Каталог знает зарегистрированные контракты и
    /// <paramref name="declared" /> — те, что нужны без регистрации («не подключено», включённый
    /// поставщик без реализации).
    /// </summary>
    public static ServiceProvider Build(string enabled, Type[] declared, params IAppModule[] modules) =>
        Services(enabled, declared, modules).BuildServiceProvider();

    public static ServiceProvider Build(string enabled, params IAppModule[] modules) =>
        Build(enabled, [], modules);

    public static IServiceCollection Services(string enabled, Type[] declared, params IAppModule[] modules)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Enabled"] = enabled })
            .Build();
        services.AddAppModules(configuration, modules);
        ModuleContractCatalog.AddTo(services, declared);
        return services;
    }

    public static IServiceCollection Services(string enabled, params IAppModule[] modules) =>
        Services(enabled, [], modules);

    public static ProbeModule Supplying(Action<IServiceCollection, string>? register) =>
        new(Supplier, SupplierTitle, register);

    public static ProbeModule Consuming(Action<IServiceCollection, string>? register = null) =>
        new(Consumer, "Потребитель", register);
}
