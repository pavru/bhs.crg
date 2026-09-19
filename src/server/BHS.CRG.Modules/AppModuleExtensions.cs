using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Modules;

/// <summary>
/// Подключение модулей к приложению: три вызова в корне композиции вместо правок по всему запуску.
/// </summary>
public static class AppModuleExtensions
{
    /// <summary>
    /// Отбирает из перечисленных модулей включённые, регистрирует их службы и кладёт реестр в
    /// контейнер.
    ///
    /// Список <paramref name="available" /> перечисляется в корне композиции руками — это
    /// единственное место, где ядро вообще знает имена модулей, и оно же граница правила «ядро о
    /// модуле не знает»: никакой сканер сборок сюда не ставится. Сканер выглядел бы удобнее, но
    /// набор модулей стал бы зависеть от того, какие файлы лежат рядом с приложением, — то есть
    /// поставка менялась бы копированием DLL, а не решением.
    ///
    /// Незнакомый код в настройке — ОТКАЗ ПРИ СТАРТЕ, а не тихий пропуск: «включил модуль, а его
    /// нет» обязано выглядеть как поломка сразу, иначе опечатка в <c>.env</c> обнаружится через
    /// неделю как «пропал раздел».
    /// </summary>
    public static IServiceCollection AddAppModules(
        this IServiceCollection services, IConfiguration configuration, params IAppModule[] available)
    {
        var codes = ModuleRegistry.ReadEnabledCodes(configuration);
        var byCode = available.ToDictionary(m => m.Code, StringComparer.OrdinalIgnoreCase);

        var unknown = codes.Where(c => !byCode.ContainsKey(c)).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                $"В Modules__Enabled названы модули, которых в этой сборке нет: {string.Join(", ", unknown)}. " +
                $"Доступны: {string.Join(", ", available.Select(m => m.Code))}.");

        var enabled = codes.Select(c => byCode[c]).ToList();

        foreach (var module in enabled)
            module.RegisterServices(services, configuration);

        services.AddSingleton(new ModuleRegistry(enabled));
        return services;
    }

    /// <summary>
    /// Регистрирует адреса включённых модулей, каждый — в СВОЕЙ группе. Группа существует
    /// отдельно от адресов даже сейчас, когда на ней ещё нет политики: ворота модуля вешаются на
    /// неё одной строкой, и тогда закрытым окажется всё, что модуль зарегистрировал, включая
    /// адреса, добавленные позже (AUTH-10).
    /// </summary>
    public static IEndpointRouteBuilder MapAppModules(this IEndpointRouteBuilder app)
    {
        var registry = app.ServiceProvider.GetRequiredService<ModuleRegistry>();

        foreach (var module in registry.Enabled)
        {
            var group = app.MapGroup(string.Empty).WithGroupName(module.Code);
            module.MapEndpoints(group);
        }

        return app;
    }

    /// <summary>
    /// Первичная инициализация включённых модулей. Вызывается при каждом старте после миграций;
    /// идемпотентность — обязанность модуля (AUTH-20).
    /// </summary>
    public static async Task InitializeAppModulesAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        var registry = services.GetRequiredService<ModuleRegistry>();
        foreach (var module in registry.Enabled)
            await module.InitializeAsync(services, ct);
    }
}
