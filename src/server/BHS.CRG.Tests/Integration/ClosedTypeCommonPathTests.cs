using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Перепись пишущих адресов записи справочника: каждый либо отказывает на объекте типа, который
/// модуль держит в своей таблице, либо назван исключением с причиной (issue #1215).
///
/// <para>Запрет «тип закрыт для общего адреса» стоял только у создания, правка его не спрашивала —
/// та самая ошибка «закрыл один вход из нескольких»: охрана на одном адресе из нескольких выглядит
/// работающей ровно так же, как на всех. Удаление и возврат из архива свободны НАРОЧНО (решение
/// владельца 07.10.2026): такая строка в общей таблице — мусор, и убрать его — благо.</para>
///
/// <para>Проверка идёт ПОВЕДЕНИЕМ и по списку адресов самого приложения, а не по исходникам: новый
/// пишущий адрес под этими путями краснеет здесь, пока о нём не принято решение, а названный
/// отказывающим действительно зовётся — на строке, положенной в базу мимо приложения (иначе ей там
/// взяться неоткуда: оба входа отказывают).</para>
/// </summary>
[Collection("Integration")]
public class ClosedTypeCommonPathTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly string[] Prefixes = ["/api/common-data", "/api/employees"];

    /// <param name="Status">
    /// Чем адрес отвечает на объект закрытого типа; <c>null</c> — в общем проходе не зовётся
    /// (чтение, свой выбор типа или действие, после которого строки нет, — у него свой тест).
    /// </param>
    /// <param name="Says">Что обязано стоять в отказе: причина, а не один код.</param>
    private sealed record Verdict(HttpStatusCode? Status, string? Says, string Why);

    private static Verdict Refuses(string says, string why) => new(HttpStatusCode.Conflict, says, why);

    private static readonly Dictionary<string, Verdict> Writes = new(StringComparer.Ordinal)
    {
        ["POST /api/common-data/"] = Refuses("не заводится", "создание: запрет стоял здесь с самого начала"),
        ["PUT /api/common-data/{id:guid}"] = Refuses("не правится", "правка заменяет запись целиком"),
        ["POST /api/common-data/{id:guid}/archive"] = Refuses("общим путём в архив",
            "своим правилом, по владельцу типа: справочник модуля в архив отправляет модуль"),

        ["POST /api/common-data/{id:guid}/unarchive"] = new(HttpStatusCode.OK, null,
            "вернуть из архива можно любую запись (ревью PR #1226): признак мог приехать копией, и " +
            "запись, которую нечем вернуть, осталась бы в архиве навсегда. Здесь строка не в архиве, " +
            "и ответ — «без изменений»; сам возврат проверяет соседний тест"),
        ["DELETE /api/common-data/{id:guid}"] = new(null, null,
            "удаление проходит: строка закрытого типа в общей таблице — мусор, и интерфейс остаётся " +
            "способом его убрать. Проверяет соседний тест — после него строки нет"),
        ["POST /api/common-data/{id:guid}/purge"] = new(null, null,
            "принудительное удаление (issue #1187) — то же тело, что у обычного: строку закрытого " +
            "типа убрать можно. В общем проходе не зовётся: без тела с числом ссылок адрес отвечает 400"),
        ["POST /api/common-data/archived-among"] = new(null, null,
            "чтение: POST только ради списка идентификаторов в теле"),
        ["POST /api/employees/"] = new(null, null,
            "тип адрес выбирает сам — «Сотрудник» ядра; назвать закрытый тип ему нечем"),
        ["PUT /api/employees/{id:guid}"] = new(HttpStatusCode.NotFound, null,
            "адрес видит только записи типа «Сотрудник»: запись другого типа для него не существует"),
        ["DELETE /api/employees/{id:guid}"] = new(HttpStatusCode.NotFound, null,
            "то же: запись другого типа для адреса не существует"),
    };

    [Fact]
    public void Каждый_пишущий_адрес_записи_назван_и_рассужден()
    {
        _ = fixture.CreateClient(); // до первого запроса служб ещё нет

        var actual = fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(route => (Route: route, Path: "/" + route.RoutePattern.RawText?.TrimStart('/')))
            .Where(r => Prefixes.Any(p => r.Path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)))
            .SelectMany(r => (r.Route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["?"])
                .Where(method => method != "GET")
                .Select(method => $"{method} {r.Path}"))
            .ToList();

        var undeclared = actual.Except(Writes.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.True(undeclared.Count == 0,
            "Появился пишущий адрес записи, о котором запрет «тип закрыт для общего адреса» не знает:\n" +
            string.Join("\n", undeclared) +
            "\n\nВпишите его в Writes: Refuses — если он обязан отказать на объекте типа, который " +
            "модуль держит в своей таблице (и позовите TypeStorageRules.EnsureCommonPathAllowed), " +
            "иначе — с причиной, почему запрет ему не нужен.");

        var stale = Writes.Keys.Except(actual, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0,
            "В переписи адреса, которых больше нет: " + string.Join(", ", stale) +
            ".\nУберите строки — иначе перепись описывает несуществующее.");
    }

    [Fact]
    public async Task Объект_закрытого_типа_общим_адресом_не_ведётся()
    {
        var (typeId, planted) = await PlantAsync();
        var client = await SignInAsync();
        var wrong = new List<string>();

        foreach (var (route, verdict) in Writes)
        {
            if (verdict.Status is not { } expected) continue;
            var response = await client.SendAsync(Request(route, typeId, planted));
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != expected)
                wrong.Add($"{route}: ждали {(int)expected}, пришло {(int)response.StatusCode} — {body}");
            else if (verdict.Says is { } says && !Unescaped(body).Contains(says, StringComparison.Ordinal))
                wrong.Add($"{route}: отказ не называет причину «{says}» — {Unescaped(body)}");
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));

        // И строка цела: отказ, после которого запись всё же изменилась, — не отказ.
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.DomainObjects.AsNoTracking().SingleAsync(o => o.CompositeTypeId == typeId);
        Assert.Equal(planted.Id, stored.Id);
        Assert.Equal("Строка", stored.DisplayName);
        Assert.Null(stored.ArchivedAt);
    }

    /// <summary>Исключение переписи — поведением: строку закрытого типа общим адресом УБРАТЬ можно.</summary>
    [Fact]
    public async Task Строку_закрытого_типа_удалить_можно()
    {
        var (typeId, planted) = await PlantAsync();
        var client = await SignInAsync();

        var response = await client.SendAsync(Request("DELETE /api/common-data/{id:guid}", typeId, planted));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var scope = fixture.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .DomainObjects.AnyAsync(o => o.Id == planted.Id));
    }

    /// <summary>
    /// Исключение переписи — поведением, а не словами: строка закрытого типа, оказавшаяся в архиве,
    /// из него ВОЗВРАЩАЕТСЯ. В общем тесте строка в архиве не была, и «200» там отвечала ветка
    /// «без изменений» — она прошла бы и при запрете на настоящем возврате (ревью PR #1233).
    /// </summary>
    [Fact]
    public async Task Строку_закрытого_типа_из_архива_вернуть_можно()
    {
        var (typeId, planted) = await PlantAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.DomainObjects.Where(o => o.Id == planted.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ArchivedAt, DateTimeOffset.UtcNow)));
        }
        var client = await SignInAsync();

        var response = await client.SendAsync(
            Request("POST /api/common-data/{id:guid}/unarchive", typeId, planted));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var check = fixture.Services.CreateScope();
        var stored = await check.ServiceProvider.GetRequiredService<AppDbContext>()
            .DomainObjects.AsNoTracking().SingleAsync(o => o.Id == planted.Id);
        Assert.Null(stored.ArchivedAt);
    }

    /// <summary>
    /// Запрос к адресу переписи. Версия у правки нарочно НЕ та: запрет обязан прийти раньше сверки
    /// версии — иначе человек получил бы «запись тем временем изменили» и пошёл бы перечитывать то,
    /// что этим адресом править нельзя вовсе.
    /// </summary>
    private static HttpRequestMessage Request(string route, Guid typeId, DomainObject planted)
    {
        var space = route.IndexOf(' ');
        var path = route[(space + 1)..].Replace("{id:guid}", planted.Id.ToString());
        var request = new HttpRequestMessage(new HttpMethod(route[..space]), path);
        if (request.Method == HttpMethod.Put)
        {
            request.Content = JsonContent.Create(new { displayName = "Подмена", data = "{}" });
            request.Headers.TryAddWithoutValidation("If-Match", "1");
        }
        else if (request.Method == HttpMethod.Post && !path.Contains(planted.Id.ToString()))
            request.Content = JsonContent.Create(new
            {
                displayName = "Вторая", compositeTypeId = typeId, data = "{}", scope = "System",
            });
        return request;
    }

    /// <summary>Тело ответа с кириллицей как есть: JSON отдаёт её и экранированной.</summary>
    private static string Unescaped(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.ToString(); }
        catch (JsonException) { return body; }
    }

    /// <summary>Тип модуля с записями в своей таблице — и строка этого типа в ОБЩЕЙ, мимо приложения.</summary>
    private async Task<(Guid TypeId, DomainObject Planted)> PlantAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var type = DocumentType.Create(
            "Запись модуля", "MOD_ROW_1215", DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[]}"""),
            "склад", TypeVisibility.Closed, storage: TypeStorage.ModuleTable);
        db.DocumentTypes.Add(type);
        var planted = DomainObject.Create(type.Id, "Строка", JsonDocument.Parse("{}"), CatalogScope.System, null);
        db.DomainObjects.Add(planted);
        await db.SaveChangesAsync();
        return (type.Id, planted);
    }

    private async Task<HttpClient> SignInAsync()
    {
        var email = $"closed_{Guid.NewGuid():N}@example.com";
        const string password = "Passw0rd!Closed";
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.CreateAsync(
                new ApplicationUser { UserName = email, Email = email, DisplayName = "Т", EmailConfirmed = true },
                password)).Succeeded);
            await users.AddToRoleAsync((await users.FindByEmailAsync(email))!, BHS.CRG.Api.Auth.SystemRoles.Admin);
        }
        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
