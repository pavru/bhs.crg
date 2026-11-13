using BHS.CRG.Modules.Exchange;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Support;

/// <summary>
/// Подмена вклада модуля в контракт пробой. Именно подмена, а не второй вклад: каталог контрактов
/// отказывает модулю с двумя реализациями одного контракта (вклад модуля один), и хост бы не стартовал.
/// </summary>
internal static class ModuleContractSwap
{
    public static IServiceCollection SwapModuleContract<TContract, TProbe>(this IServiceCollection services, string module)
        where TContract : class
        where TProbe : class, TContract
    {
        var own = services.Where(d =>
                (d.ImplementationInstance is ModuleContractRegistration r && r.Contract == typeof(TContract) && r.Module == module)
                || (d.IsKeyedService && d.ServiceType == typeof(TContract) && Equals(d.ServiceKey, module)))
            .ToList();
        foreach (var d in own) services.Remove(d);
        return services.AddModuleContract<TContract, TProbe>(module);
    }
}
