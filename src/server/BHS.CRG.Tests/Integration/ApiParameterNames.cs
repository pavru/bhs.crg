using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Имена, которыми адреса API принимают значения, — для сторожей «понятие клиента не уходит на
/// сервер»: рабочее пространство (<see cref="WorkspaceIsNotApiParameterTests" />) и избранные стройки
/// (<see cref="FavoritesAreNotApiParameterTests" />). Разбор один, чтобы оба сторожа видели одно и то
/// же: поправленный в одном месте разбор (ревью PR #1000 — наборы базы) не остался бы слепым в другом.
///
/// <para>⚠️ Чего он НЕ видит: имя, собранное в строку в теле запроса (<c>Filter = "workspace=…"</c>),
/// и вложенные типы глубже первого уровня. Поймать это нечем, кроме чтения кода; зато прямой путь —
/// параметр адреса, строка запроса, поле тела — закрыт.</para>
/// </summary>
internal static class ApiParameterNames
{
    /// <summary>
    /// Всё, чем адрес может принять значение: параметр маршрута, параметр обработчика (строка
    /// запроса, заголовок, тело) и поля типа, пришедшего телом.
    /// </summary>
    public static List<(string Route, string Where, string Name)> Collect(IntegrationTestFixture fixture)
    {
        _ = fixture.CreateClient();
        var endpoints = fixture.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>();

        // Службу от тела запроса отличает КОНТЕЙНЕР, а не догадка по имени типа.
        var isService = fixture.Services.GetRequiredService<IServiceProviderIsService>();

        var names = new List<(string, string, string)>();

        foreach (var endpoint in endpoints)
        {
            var route = "/" + (endpoint.RoutePattern.RawText ?? "").Trim('/');

            foreach (var parameter in endpoint.RoutePattern.Parameters)
                names.Add((route, "параметр маршрута", parameter.Name));

            foreach (var parameter in endpoint.Metadata.GetMetadata<MethodInfo>()?.GetParameters() ?? [])
            {
                names.Add((route, "параметр обработчика", parameter.Name ?? ""));

                foreach (var property in BodyProperties(parameter.ParameterType, isService))
                    names.Add((route, "поле тела", property));
            }
        }

        return names;
    }

    /// <summary>
    /// Положительные якоря ПЕРЕД запретом. Отрицательное утверждение проходит и на пустом списке:
    /// сломайся разбор метаданных — сторож молчал бы, ничего не проверяя (грань, пойманная ревью #988).
    /// </summary>
    public static void AssertParsed(List<(string Route, string Where, string Name)> named)
    {
        Assert.True(named.Count > 50, $"Сторож не разобрал адреса: собрано {named.Count} имён");
        Assert.Contains(named, x => x.Name == "setId");

        // Тело разбирается — у запроса видны его поля…
        Assert.Contains(named, x => x.Where == "поле тела" && x.Name == "DisplayName");
        // …а наборы базы полями тела НЕ считаются: AppDbContext внедряется прямо в обработчики, и
        // его DbSet-ы попадали сюда до правки по ревью PR #1000. С ними сторож однажды упал бы на
        // невинном адресе — стоило появиться набору с подходящим именем.
        Assert.DoesNotContain(named, x => x.Where == "поле тела" && x.Name == "Notifications");
    }

    /// <summary>Имена, содержащие любое из запрещённых слов, — строками для сообщения.</summary>
    public static List<string> Matching(List<(string Route, string Where, string Name)> named, string[] banned) =>
        named
            .Where(x => banned.Any(b => x.Name.Contains(b, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.Route} → {x.Where} «{x.Name}»")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Поля того, что приходит ТЕЛОМ: наш тип, который контейнер выдать не может.
    ///
    /// ⚠️ Проверка «наш ли тип» сама по себе недостаточна, и это нашло ревью PR #1000: наши службы
    /// тоже наши. <c>AppDbContext</c> внедряется прямо в обработчики, и его наборы (<c>DbSet</c>)
    /// собирались здесь как «поля тела» — сторож проходил лишь потому, что среди них нет набора с
    /// подходящим именем. Появись <c>DbSet&lt;Workspace&gt;</c> — и он упал бы на невинных адресах
    /// с сообщением, называющим не то.
    ///
    /// Отличает службу от тела КОНТЕЙНЕР (<see cref="IServiceProviderIsService" />), а не догадка:
    /// ровно он решает это и в работе приложения, когда связывает параметры обработчика.
    /// </summary>
    private static IEnumerable<string> BodyProperties(Type type, IServiceProviderIsService isService) =>
        type.FullName?.StartsWith("BHS.CRG", StringComparison.Ordinal) == true && !isService.IsService(type)
            ? type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name)
            : [];
}
