using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Рабочее пространство — понятие КЛИЕНТА (issue #954, ТЗ AUTH-16.2). В запросы к серверу оно не
/// передаётся и ни в одной проверке доступа не участвует.
///
/// Почему это сторож, а не строка в ТЗ. Формулировку «пространство читает только навигация» тестом
/// не взять — навигация живёт на клиенте. Зато проверяемо обратное: ни один адрес не ПРИНИМАЕТ
/// пространство. Появится параметр — появится и соблазн по нему отобрать, а отбор по пространству
/// даёт пустой список, неотличимый от «данных нет» (OVW-9). Это уже случалось с уровнями
/// <c>CatalogScope</c>: механизм организации данных приняли за разграничение доступа.
///
/// ⚠️ Сторож заведён РАНЬШЕ самих пространств — нарочно. Переключатель отложен до появления второго
/// модуля (решение 22.09.2026: при одном модуле сужать нечего, и переключателя по AUTH-16.1 нет
/// вовсе), а правило нужно именно к тому дню, когда пространства будут делать, — и, скорее всего,
/// не тот, кто читал это ТЗ. Тест переносит правило через отсрочку.
///
/// ⚠️ Чего он НЕ видит: имя, собранное в строку в теле запроса (<c>Filter = "workspace=…"</c>), и
/// вложенные типы глубже первого уровня. Поймать это нечем, кроме чтения кода; зато прямой путь —
/// параметр адреса, строка запроса, поле тела — закрыт.
/// </summary>
[Collection("Integration")]
public class WorkspaceIsNotApiParameterTests(IntegrationTestFixture fixture)
{
    /// <summary>
    /// Слова, которыми пространство назвали бы. Русское — потому что имена полей в этом проекте
    /// бывают и русскими, а «workspace» рядом с «пространством» в одной таблице выглядело бы
    /// исчерпывающим списком, не будучи им.
    /// </summary>
    private static readonly string[] Banned = ["workspace", "пространств"];

    [Fact]
    public void Ни_один_адрес_не_принимает_рабочее_пространство()
    {
        var named = NamesByRoute();

        // Положительный якорь ПЕРЕД запретом. Отрицательное утверждение проходит и на пустом
        // списке: сломайся разбор метаданных — сторож молчал бы, ничего не проверяя (грань,
        // пойманная ревью #988).
        Assert.True(named.Count > 50, $"Сторож не разобрал адреса: собрано {named.Count} имён");
        Assert.Contains(named, x => x.Name == "setId");

        // Тело разбирается — у запроса видны его поля…
        Assert.Contains(named, x => x.Where == "поле тела" && x.Name == "DisplayName");
        // …а наборы базы полями тела НЕ считаются: AppDbContext внедряется прямо в обработчики, и
        // его DbSet-ы попадали сюда до правки по ревью PR #1000. С ними сторож однажды упал бы на
        // невинном адресе — стоило появиться набору с подходящим именем.
        Assert.DoesNotContain(named, x => x.Where == "поле тела" && x.Name == "Notifications");

        var found = named
            .Where(x => Banned.Any(b => x.Name.Contains(b, StringComparison.OrdinalIgnoreCase)))
            .Select(x => $"{x.Route} → {x.Where} «{x.Name}»")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(found.Count == 0,
            "Адрес принимает рабочее пространство. Пространство — понятие клиента (ТЗ AUTH-16.2): "
            + "оно не передаётся на сервер и ни в одной проверке не участвует. Отбор по нему даёт "
            + "пустой список, неотличимый от «данных нет»:\n  " + string.Join("\n  ", found));
    }

    /// <summary>
    /// Всё, чем адрес может принять значение: параметр маршрута, параметр обработчика (строка
    /// запроса, заголовок, тело) и поля типа, пришедшего телом.
    /// </summary>
    private List<(string Route, string Where, string Name)> NamesByRoute()
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
