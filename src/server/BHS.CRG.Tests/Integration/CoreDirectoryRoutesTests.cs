using BHS.CRG.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Справочники ядра — стройки с разделами (ТЗ CORE-5, issue #960) и каталог организаций и лиц
/// (ТЗ CORE-6, issue #961) — принадлежат ЯДРУ и переживают выключение любого модуля.
///
/// <para>Зачем сторож. На стройках стоит весь <c>CatalogScope</c>: наборы данных, шаблоны,
/// профили уровней и комплекты адресуют уровни через стройку и раздел. Уедь хоть один из этих
/// адресов под модуль — выключение модуля унесло бы общий справочник, и выглядело бы это не как
/// «модуль выключен», а как «пропали наборы».</para>
///
/// <para>⚠️ Обратная половина важна не меньше: под путями справочника не должно оказаться адресов,
/// которые заводят или правят ЧУЖИЕ сущности. До 0.192.0 комплект документов создавался запросом
/// <c>POST /api/sections/{id}/sets</c> — под префиксом ядра, то есть при выключенном модуле
/// комплекты можно было бы заводить, а открыть ни один из них нельзя. Адрес переехал к комплектам.
/// Поэтому список ниже — белый, а не правило: новый адрес под этими путями обязан пройти через
/// решение, а не появиться молча.</para>
/// </summary>
[Collection("Integration")]
public class CoreDirectoryRoutesTests(IntegrationTestFixture fixture)
{
    /// <summary>Пути справочников ядра. Всё, что под ними, обязано быть ядром и только справочником.</summary>
    private static readonly string[] DirectoryPrefixes = ["/api/constructions", "/api/sections", "/api/catalog"];

    private static readonly string[] Expected =
    [
        "DELETE /api/constructions/{id:guid}",
        "GET /api/constructions/",
        "GET /api/constructions/{id:guid}",
        "POST /api/constructions/",
        "POST /api/constructions/{constructionId:guid}/sections",
        "PUT /api/constructions/{id:guid}",
        // Пояс и внешний идентификатор — свойства САМОЙ стройки (ТЗ CORE-5), поэтому им здесь место.
        "PUT /api/constructions/{id:guid}/timezone",
        "PUT /api/constructions/{id:guid}/external-id",
        "DELETE /api/sections/{id:guid}",
        "PUT /api/sections/{id:guid}",

        // Каталог организаций и лиц (ТЗ CORE-6). Переезд состоялся раньше этой записи: типы
        // отданы ядру миграцией владельцев (issue #955), адреса закрыты core.catalog.* воротами
        // (issue #947) и регистрируются корнем композиции. Запись же держит переезд: без неё
        // каталог мог бы уехать к модулю одной строкой в RoutePrefixes — и организации исчезли бы
        // вместе с выключенным модулем, а с ними реквизиты во ВСЕХ документах.
        "GET /api/catalog/",
        "GET /api/catalog/{id:guid}",
        "POST /api/catalog/",
        "PUT /api/catalog/{id:guid}",
        "DELETE /api/catalog/{id:guid}",
    ];

    [Fact]
    public void Core_directories_belong_to_the_core_and_hold_nothing_else()
    {
        // Клиент нужен, чтобы хост поднялся: до первого запроса служб ещё нет.
        _ = fixture.CreateClient();

        var actual = new List<string>();
        var owned = new List<string>();

        foreach (var endpoint in fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints)
        {
            if (endpoint is not RouteEndpoint route) continue;
            var path = "/" + route.RoutePattern.RawText?.TrimStart('/');
            if (!DirectoryPrefixes.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)
                                            || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
                continue;

            var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["?"];
            foreach (var method in methods) actual.Add($"{method} {path}");

            // Владелец-модуль у адреса справочника — это и есть переезд справочника в модуль.
            if (route.Metadata.GetMetadata<AppModuleEndpoint>() is { } owner)
                owned.Add($"{owner.Code}: {path}");
        }

        Assert.True(owned.Count == 0,
            "Адрес справочника ядра зарегистрирован модулем: " + string.Join(", ", owned) + ".\n" +
            "Выключение этого модуля унесло бы общий справочник, на котором стоит CatalogScope:\n" +
            "наборы данных, шаблоны и профили уровней адресуют уровни через стройку и раздел.");

        var unexpected = actual.Except(Expected, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var missing = Expected.Except(actual, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.True(unexpected.Count == 0,
            "Под путями справочника ядра появился новый адрес: " + string.Join(", ", unexpected) + ".\n" +
            "Если он правит стройку или раздел — допишите его в список. Если он заводит или правит\n" +
            "что-то другое (комплект, документ, отчёт), ему здесь не место: под префиксом ядра он\n" +
            "переживёт выключение своего модуля и будет работать в отсутствие того, чем правит.");

        Assert.True(missing.Count == 0,
            "Адрес справочника ядра исчез: " + string.Join(", ", missing) + ".\n" +
            "Либо он переехал — тогда поправьте список, либо справочник потерял дверь.");
    }
}
