using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
        var disabled = available.Except(enabled).ToList();

        // Службы регистрирует только включённый модуль: выключенный не должен висеть в контейнере
        // и попадать в фоновые задания (AUTH-19). Его адреса при этом всё равно появятся — отказом,
        // см. MapAppModules.
        foreach (var module in enabled)
            module.RegisterServices(services, configuration);

        services.AddSingleton(new ModuleRegistry(enabled, disabled));
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

        foreach (var module in registry.Disabled)
            module.MapEndpoints(DisabledGroup(app, module));

        return app;
    }

    /// <summary>
    /// Группа выключенного модуля: адреса те же, ответ — отказ с названной причиной.
    ///
    /// Почему адреса выключенного модуля вообще регистрируются. Незарегистрированный адрес отвечает
    /// пустым 404 — тем же, что и опечатка в ссылке. По ТЗ выключенный модуль обязан отвечать
    /// ОТКАЗОМ, а не пустым ответом (OVW-10), а клиент обязан показать честную страницу «нужен
    /// модуль X» вместо бесконечной загрузки (AUTH-15). Различить это можно только по ответу.
    ///
    /// Почему не заглушка по префиксу, что было бы проще. Префиксы модуля и ядра пересекаются:
    /// печатные формы исполнительной документации живут под <c>/api/document-sets</c>, где рядом
    /// стоят общие адреса комплектов. Заглушка на префикс накрыла бы и их — то есть выключение
    /// модуля унесло бы часть ядра. Поэтому отказ вешается на ТЕ ЖЕ маршруты, которые объявляет сам
    /// модуль: ни одного лишнего адреса, ни одного забытого.
    ///
    /// Фильтр группы срабатывает ДО обработчика, поэтому код обработчика не выполняется и службы,
    /// которых выключенный модуль не регистрировал, не понадобятся. Отсюда требование к модулю:
    /// <see cref="IAppModule.MapEndpoints" /> не должен трогать службы В МОМЕНТ РЕГИСТРАЦИИ — только
    /// при обработке запроса.
    /// </summary>
    private static IEndpointRouteBuilder DisabledGroup(IEndpointRouteBuilder app, IAppModule module)
    {
        var group = app.MapGroup(string.Empty);

        group.AddEndpointFilter((context, _) => ValueTask.FromResult<object?>(
            Results.Json(
                new
                {
                    error = $"Модуль «{module.Title}» не подключён на этом экземпляре.",
                    module = module.Code,
                },
                statusCode: StatusCodes.Status501NotImplemented)));

        // Из описания API адреса выключенного модуля убираются: документ описывает то, что
        // экземпляр УМЕЕТ, а не то, на что он отвечает отказом.
        group.ExcludeFromDescription();

        return group;
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
