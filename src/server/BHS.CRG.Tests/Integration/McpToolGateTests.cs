using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Mcp;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Ворота инструментов MCP в работе (issue #948, ТЗ AUTH-12.1).
///
/// ⚠️ Всё — ЧЕРЕЗ ЖИВОЙ ВХОД и по HTTP. Ворота ставятся фильтрами транспорта, и вызов метода
/// инструмента напрямую их не проходит вовсе: тест, дёргающий метод, подтвердил бы только то, что
/// метод работает. Ровно этим отличается «право объявлено» от «право проверяется».
///
/// Роль взята «Монтажник» — у неё есть чтение справочников ядра и нет ни одного права
/// исполнительной документации. На ней и видно то, ради чего задача: агент с чужим токеном не
/// получает через MCP того, чего ему не даёт интерфейс.
/// </summary>
[Collection("Integration")]
public class McpToolGateTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Password = "Passw0rd!MCP";

    /// <summary>
    /// Что видит «Монтажник» — ПОЛНЫМ списком, а не «не содержит лишнего».
    ///
    /// Список полный НАРОЧНО: новый инструмент, забывший ворота, попадёт сюда сам и уронит тест. С
    /// проверкой «не содержит вот этих трёх» он прошёл бы молча — а именно так забытые ворота и
    /// выглядят: инструмент работает и отвечает данными.
    /// </summary>
    private static readonly string[] InstallerTools =
    [
        "get_catalog_entry", "get_construction", "get_dataset", "get_document_type", "get_job",
        "get_rows", "get_source", "list_catalog_entries", "list_constructions", "list_datasets",
    ];

    [Fact]
    public async Task Монтажник_видит_только_инструменты_своих_прав()
    {
        var installer = await SignInAsync("Installer");

        var tools = await ToolsAsync(installer);

        Assert.Equal(InstallerTools.Order(), tools.Order());
    }

    /// <summary>
    /// Обратная сторона: у кого права есть, у того инструменты на месте. Без этого «список отобран»
    /// нельзя отличить от «список пуст», а отказ, переодетый в пустоту, — худший из исходов.
    /// </summary>
    [Fact]
    public async Task Инженер_ИД_видит_инструменты_исполнительной_документации()
    {
        var engineer = await SignInAsync(SystemRoles.IdEngineer);

        var tools = await ToolsAsync(engineer);

        Assert.Contains("get_document_set", tools);
        Assert.Contains("generate_document", tools);
        // Ворота модуля, а не права: библиотека документов качества своего права пока не носит.
        Assert.Contains("list_quality_documents", tools);
        Assert.Contains("list_reconciliations", tools);
    }

    /// <summary>
    /// «Администратор» получает всё объявленное — значит, и все инструменты. Иначе ворота могли бы
    /// прятать инструмент от того, у кого есть все права, и заметить это было бы некому.
    /// </summary>
    [Fact]
    public async Task Администратор_видит_все_объявленные_инструменты()
    {
        var admin = await SignInAsync(SystemRoles.Admin);

        var tools = await ToolsAsync(admin);

        Assert.Equal(AllDeclaredTools().Order(), tools.Order());
    }

    /// <summary>
    /// Прямой вызов недоступного инструмента — ОТКАЗ, а не пустой результат (граница из issue).
    /// Пустой результат агент прочтёт как «данных нет» и построит на этом вывод: «комплекта не
    /// существует» вместо «вам его не показывают».
    /// </summary>
    [Fact]
    public async Task Недоступный_инструмент_отвечает_отказом_а_не_пустотой()
    {
        var installer = await SignInAsync("Installer");

        var answer = await McpTestClient.TryCallAsync(installer, "tools/call",
            new { name = "get_document_set", arguments = new { setId = Guid.NewGuid() } });

        Assert.True(answer.TryGetProperty("error", out var error),
            "Недоступный инструмент ответил без отказа: " + answer);
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));
    }

    /// <summary>
    /// Ресурсы и промпты отбираются теми же правами. Витрина прикрепления собирается своим
    /// обработчиком (<see cref="McpResourceCatalog" />), фильтры SDK её не видят — и без отдельного
    /// отбора она показывала бы имена комплектов тому, кому чтение комплекта закрыто.
    /// </summary>
    [Fact]
    public async Task Витрина_и_промпты_отбираются_теми_же_правами()
    {
        var (constructionId, setId) = await SeedAsync();
        var installer = await SignInAsync("Installer");
        var engineer = await SignInAsync(SystemRoles.IdEngineer);

        var installerUris = await ResourceUrisAsync(installer);
        Assert.Contains($"bhs://construction/{constructionId}", installerUris);
        Assert.DoesNotContain($"bhs://document-set/{setId}", installerUris);

        var engineerUris = await ResourceUrisAsync(engineer);
        Assert.Contains($"bhs://document-set/{setId}", engineerUris);

        // Промпты сверки требуют права вести сверки: у «Монтажника» его нет, у инженера ИД есть.
        Assert.Empty(McpTestClient.NamesOf(
            await McpTestClient.CallAsync(installer, "prompts/list"), "prompts"));
        Assert.NotEmpty(McpTestClient.NamesOf(
            await McpTestClient.CallAsync(engineer, "prompts/list"), "prompts"));
    }

    /// <summary>
    /// Два права витрины — НЕЗАВИСИМЫ (нашло ревью PR #998). Комплекты были вложены в ветку права
    /// на стройки, то есть требовали обоих: у роли, которой можно читать документы и нельзя —
    /// справочник строек, витрина приходила пустой при работающем get_document_set. Пустой список
    /// там, где отказ, — худший из исходов: прикрепить комплект неоткуда, и не сказано почему.
    /// </summary>
    [Fact]
    public async Task Комплекты_в_витрине_не_требуют_права_на_стройки()
    {
        var (_, setId) = await SeedAsync();
        var documents = await SignInWithPermissionsAsync("id.document.read");

        var uris = await ResourceUrisAsync(documents);

        Assert.Contains($"bhs://document-set/{setId}", uris);
        // А стройки — не показываются: право на них своё, и его нет.
        Assert.DoesNotContain(uris, u => u.StartsWith("bhs://construction/"));
    }

    private static async Task<IReadOnlyList<string>> ToolsAsync(HttpClient client)
        => McpTestClient.NamesOf(await McpTestClient.CallAsync(client, "tools/list"), "tools");

    private static async Task<List<string>> ResourceUrisAsync(HttpClient client)
        => [.. (await McpTestClient.CallAsync(client, "resources/list"))
            .GetProperty("resources").EnumerateArray()
            .Select(r => r.GetProperty("uri").GetString()!)];

    /// <summary>Все объявленные инструменты — тем же перечислением, каким их видит SDK.</summary>
    private static IEnumerable<string> AllDeclaredTools() =>
        new[]
        {
            typeof(DataSnapshotTools), typeof(DomainSnapshotTools), typeof(DocumentActionTools),
            typeof(ObservationTools), typeof(ReconciliationTools), typeof(JobTools),
            typeof(OperationTools),
        }
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
        .Where(a => a is not null)
        .Select(a => a!.Name!);

    private async Task<(Guid ConstructionId, Guid SetId)> SeedAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();

        var type = await m.Send(new CreateDocumentTypeCommand(
            "Акт", $"ACT_{Guid.NewGuid():N}"[..12], DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}""")));
        var construction = await m.Send(new CreateConstructionCommand("ДНС Сити", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "250701.ЭОМ-1"));
        await m.Send(new AddDocumentToSetCommand(set.Id, type.Id));

        return (construction.Id, set.Id);
    }

    /// <summary>
    /// Клиент с ролью, состав которой перечислен здесь: системной роли с нужным сочетанием прав
    /// может не быть, а сочетание — как раз то, что проверяется.
    /// </summary>
    private async Task<HttpClient> SignInWithPermissionsAsync(params string[] permissions)
    {
        var roleName = $"McpGate_{Guid.NewGuid():N}";

        using (var scope = fixture.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            Assert.True((await roles.CreateAsync(new IdentityRole<Guid>(roleName))).Succeeded);
            var role = (await roles.FindByNameAsync(roleName))!;
            foreach (var code in permissions)
                Assert.True((await roles.AddClaimAsync(
                    role, new Claim(RoleSynchronizer.PermissionClaim, code))).Succeeded);
        }

        return await SignInAsync(roleName);
    }

    private async Task<HttpClient> SignInAsync(string role)
    {
        var email = $"mcp_{Guid.NewGuid():N}@example.com";

        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email, Email = email, DisplayName = "Агент", EmailConfirmed = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
