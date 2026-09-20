using BHS.CRG.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Каждый адрес включённого модуля лежит под одним из путей, которые модуль объявил
/// (<see cref="IAppModule.RoutePrefixes" />).
///
/// Зачем это стеречь. Пути объявляются отдельно от адресов — иначе выключенному модулю нечем
/// отвечать: его адреса не строятся вовсе. Раз списка два, они разойдутся: кто-то добавит модулю
/// адрес под новым путём и про объявление не вспомнит. Разойдутся они молча, и всплывёт это не
/// здесь, а у заказчика с выключенным модулем — новый адрес ответит пустым 404 вместо отказа, и
/// человек пойдёт искать опечатку в ссылке.
///
/// Тест берёт НАСТОЯЩЕЕ приложение, а не выдуманный модуль: проверять договор на подделке —
/// значит проверять подделку.
/// </summary>
[Collection("Integration")]
public class ModulePrefixCoverageTests(IntegrationTestFixture fixture)
{
    [Fact]
    public void Every_module_endpoint_lives_under_a_declared_prefix()
    {
        // Клиент нужен, чтобы хост поднялся: до первого запроса служб ещё нет.
        _ = fixture.CreateClient();

        var registry = fixture.Services.GetRequiredService<ModuleRegistry>();
        var prefixes = registry.Enabled.ToDictionary(m => m.Code, m => m.RoutePrefixes);

        var offenders = new List<string>();
        foreach (var endpoint in fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints)
        {
            if (endpoint is not RouteEndpoint route) continue;
            if (route.Metadata.GetMetadata<AppModuleEndpoint>() is not { } owner) continue;
            if (!prefixes.TryGetValue(owner.Code, out var declared)) continue;

            var path = "/" + route.RoutePattern.RawText?.TrimStart('/');
            if (!declared.Any(p => CoveredBy(path, p)))
                offenders.Add($"{owner.Code}: {path}");
        }

        Assert.True(offenders.Count == 0,
            "Адрес модуля лежит вне объявленных им путей: " + string.Join(", ", offenders) + ".\n" +
            "Такой адрес при выключенном модуле ответит пустым 404 вместо отказа «модуль не\n" +
            "подключён». Допишите путь в RoutePrefixes модуля — или перенесите адрес под уже\n" +
            "объявленный.");
    }

    /// <summary>
    /// Покрывает ли объявленный путь адрес — ПО СЕГМЕНТАМ, а не посимвольно.
    ///
    /// Отказ строится группой по пути, то есть по границе сегмента: <c>/api/plans</c> накрывает
    /// <c>/api/plans</c> и <c>/api/plans/...</c>, но не <c>/api/plans-summary/...</c>. Посимвольное
    /// сравнение считало последний покрытым, и адрес прошёл бы сторожа зелёным, а при выключенном
    /// модуле ответил бы пустым 404 — ровно тем расхождением, ради которого сторож и написан
    /// (ревью #969).
    /// </summary>
    private static bool CoveredBy(string path, string prefix) =>
        path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(prefix.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Пути модуля не пусты: модуль без объявленных путей выключается «в тишину» — ни одного
    /// отказа, все его адреса отвечают пустым 404.
    /// </summary>
    [Fact]
    public void Every_module_declares_at_least_one_prefix()
    {
        _ = fixture.CreateClient();

        var silent = fixture.Services.GetRequiredService<ModuleRegistry>().Enabled
            .Where(m => m.RoutePrefixes.Count == 0)
            .Select(m => m.Code)
            .ToList();

        Assert.True(silent.Count == 0,
            "Модуль не объявил ни одного пути: " + string.Join(", ", silent) + ".\n" +
            "Выключение такого модуля не даст ни одного отказа — все его адреса просто исчезнут.");
    }
}
