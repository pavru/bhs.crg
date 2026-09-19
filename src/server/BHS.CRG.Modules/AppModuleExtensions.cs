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
        var codes = ModuleRegistry.ReadEnabledCodes(configuration, out var fromDefault);
        var byCode = available.ToDictionary(m => m.Code, StringComparer.OrdinalIgnoreCase);

        var unknown = codes.Where(c => !byCode.ContainsKey(c)).ToList();
        if (unknown.Count > 0)
            // Про умолчание говорим отдельно: иначе сборка без модуля исполнительной документации
            // отказывала бы словами «в Modules__Enabled названы модули, которых нет: id» — при
            // пустой переменной. Человек читает это как «я такого не писал» и ищет не там
            // (ревью #968).
            throw new InvalidOperationException(fromDefault
                ? $"Modules__Enabled не задан, а модуля по умолчанию «{ModuleRegistry.DefaultCode}» " +
                  $"в этой сборке нет. Укажите набор явно. Доступны: {string.Join(", ", available.Select(m => m.Code))}."
                : $"В Modules__Enabled названы модули, которых в этой сборке нет: {string.Join(", ", unknown)}. " +
                  $"Доступны: {string.Join(", ", available.Select(m => m.Code))}.");

        var enabled = codes.Select(c => byCode[c]).ToList();

        foreach (var module in enabled)
            module.RegisterServices(services, configuration);

        services.AddSingleton(new ModuleRegistry(enabled));
        return services;
    }

    /// <summary>
    /// Регистрирует адреса включённых модулей, каждый — в СВОЕЙ группе, и группа **закрыта**:
    /// всё, что модуль в ней зарегистрировал, требует вошедшего пользователя, включая адреса,
    /// добавленные позже (AUTH-10).
    ///
    /// ⚠️ Ворота ставятся здесь, а не оставляются «на потом». Умолчания у приложения нет
    /// (<c>FallbackPolicy</c> не задан), поэтому адрес без явной авторизации анонимен — и первый
    /// же модуль, который поверит обещанию «свою авторизацию ставить не нужно», молча открыл бы
    /// свои данные без токена (поймано на ревью #968). Сейчас это не видно только потому, что
    /// обёртка исполнительной документации ставит авторизацию внутри своих подгрупп сама.
    ///
    /// Пока это базовые ворота «вошёл». Политика модуля (<c>module:&lt;код&gt;</c>, AUTH-8) заменит
    /// их в задаче про проверку прав — там же, где появятся сами политики.
    ///
    /// ⚠️ Имя группе НЕ даётся (<c>WithGroupName</c>), и это не упущение. Имя группы в ASP.NET — это
    /// имя ДОКУМЕНТА OpenAPI, а не ярлык: адрес с именем «id» попадает в документ «id», которого
    /// никто не заводил, и исчезает из «v1» — единственного, который создаёт <c>AddOpenApi()</c>.
    /// Поймано на ревью #968: адреса модуля отвечали 401, то есть работали, а из описания API
    /// пропали все до одного. Отказ, переодетый в результат: приложение исправно, документ неполон,
    /// и заметит это тот, кто будет писать по нему клиента.
    /// </summary>
    public static IEndpointRouteBuilder MapAppModules(this IEndpointRouteBuilder app)
    {
        var registry = app.ServiceProvider.GetRequiredService<ModuleRegistry>();

        foreach (var module in registry.Enabled)
            module.MapEndpoints(app.MapGroup(string.Empty).RequireAuthorization());

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
