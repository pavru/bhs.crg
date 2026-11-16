using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Tests.Mcp;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Действие агента через MCP видно в журнале (ТЗ CORE-23, CORE-28, issue #1336, design D4).
///
/// <para><b>Сторож</b> берёт инструменты из описи, сверенной с регистрацией (<see cref="McpInventory" />),
/// а фильтры — из опций собранного сервера: каждый зарегистрированный фильтр вызовов прогоняется на каждом
/// инструменте по отдельности вокруг удавшегося вызова. Снять фильтр журнала из регистрации — и меняющие
/// инструменты останутся без записи, а тест назовёт их поимённо.</para>
///
/// <para><b>Сквозной прогон</b> идёт по HTTP через <c>/mcp</c>: канал записи даёт метка адреса, и вызов
/// метода инструмента напрямую её бы не проверил.</para>
/// </summary>
[Collection("Integration")]
public class McpToolJournalTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Password = "Passw0rd!Journal";

    [Fact]
    public async Task Каждый_меняющий_инструмент_описи_пишет_журнал_а_читающий_нет()
    {
        var options = fixture.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var filters = options.Filters.Request.CallToolFilters;
        Assert.NotEmpty(filters);

        var registered = (options.ToolCollection ?? []).ToDictionary(t => t.ProtocolTool.Name, StringComparer.Ordinal);
        var tools = McpInventory.FromHost(fixture.Services)
            .Where(p => p.Kind == McpKind.Tool && registered.ContainsKey(p.Name)).ToList();
        Assert.NotEmpty(tools);

        var accessor = fixture.Services.GetRequiredService<IHttpContextAccessor>();
        var agent = Guid.NewGuid();
        // Контексту вызова нужен сервер; фильтру журнала он не нужен, поэтому — несвязанный, на пустых потоках.
        await using var server = McpServer.Create(
            new StreamServerTransport(Stream.Null, Stream.Null), new McpServerOptions());
        var silent = new List<string>();
        var noisy = new List<string>();

        foreach (var tool in tools)
        {
            var before = await CountAsync(tool.Name);

            accessor.HttpContext = McpRequest(agent);
            try
            {
                foreach (var filter in filters)
                {
                    using var scope = fixture.Services.CreateScope();
                    var handler = filter((_, _) => ValueTask.FromResult(new CallToolResult()));
                    var context = new RequestContext<CallToolRequestParams>(server, new JsonRpcRequest { Method = "tools/call" })
                    {
                        Params = new CallToolRequestParams
                        {
                            Name = tool.Name,
                            Arguments = new Dictionary<string, JsonElement>
                            {
                                ["setId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString()),
                            },
                        },
                        MatchedPrimitive = registered[tool.Name],
                        Services = scope.ServiceProvider,
                    };
                    // Чужие фильтры (ворота прав) на подставном вызове вправе отказать — проверяется только то,
                    // что после удавшегося вызова пишет журнал.
                    try { await handler(context, CancellationToken.None); }
                    catch (Exception) { /* отказ чужого фильтра — не запись */ }
                }
            }
            finally
            {
                accessor.HttpContext = null;
            }

            var written = await RowsAsync(tool.Name);
            var added = written.Count - before;
            if (tool.Tool.ReadOnly) { if (added != 0) noisy.Add(tool.Name); }
            else
            {
                if (added != 1) { silent.Add($"{tool.Name} (записей: {added})"); continue; }
                var row = written[0];
                Assert.Equal("mcp", row.Channel);
                Assert.Equal(agent, row.ActorId);
            }
        }

        Assert.True(silent.Count == 0,
            "Меняющие инструменты без записи журнала — фильтр McpToolJournal не зарегистрирован или их не видит: "
            + string.Join(", ", silent));
        Assert.True(noisy.Count == 0, "Читающие инструменты пишут журнал: " + string.Join(", ", noisy));
    }

    [Fact]
    public async Task Сообщённое_агентом_замечание_видно_в_журнале_с_автором_и_каналом_mcp()
    {
        var setId = await SeedSetAsync();
        var (agent, agentId) = await SignInAsync(SystemRoles.Admin, "Агент Петрова");

        await McpTestClient.CallAsync(agent, "tools/call", new
        {
            name = "report_observation",
            arguments = new
            {
                setId,
                key = "аоср-1.проба",
                title = "Проба журнала",
                references = new { documentIds = new[] { Guid.NewGuid() } },
            },
        });

        var row = Assert.Single(await RowsAsync("report_observation"));
        Assert.Equal("mcp", row.Channel);
        Assert.Equal(agentId, row.ActorId);
        Assert.Equal("Агент Петрова", row.ActorName);
        Assert.Equal(setId.ToString(), row.TargetId);
    }

    [Fact]
    public async Task Отказ_прав_и_ошибка_инструмента_журнал_не_пишут()
    {
        var setId = await SeedSetAsync();
        var (installer, _) = await SignInAsync("Installer", "Монтажник");
        var (admin, _) = await SignInAsync(SystemRoles.Admin, "Администратор");

        var denied = await McpTestClient.TryCallAsync(installer, "tools/call", new
        {
            name = "report_observation",
            arguments = new { setId, key = "к", title = "т", references = new { documentIds = new[] { Guid.NewGuid() } } },
        });
        Assert.True(denied.TryGetProperty("error", out _), "Вызов без права прошёл: " + denied);

        // Пустой ключ — отказ самого инструмента (isError или ошибка протокола), а не удавшийся вызов.
        await McpTestClient.TryCallAsync(admin, "tools/call", new
        {
            name = "report_observation",
            arguments = new { setId, key = "", title = "т", references = new { documentIds = new[] { Guid.NewGuid() } } },
        });

        Assert.Empty(await RowsAsync("report_observation"));
    }

    private static DefaultHttpContext McpRequest(Guid userId)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()), new Claim("displayName", "Агент")], "Test")),
        };
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(ActivityChannel.Mcp), "mcp"));
        return context;
    }

    private async Task<int> CountAsync(string tool) => (await RowsAsync(tool)).Count;

    private async Task<List<ActivityRecord>> RowsAsync(string tool)
    {
        using var scope = fixture.Services.CreateScope();
        var journal = scope.ServiceProvider.GetRequiredService<IActivityLog>();
        return [.. (await journal.ExportAsync())
            .Where(r => r.Action == ActivityActions.McpToolCalled.Code && r.After == tool)
            .OrderByDescending(r => r.OccurredAt)];
    }

    private async Task<Guid> SeedSetAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var type = await m.Send(new CreateDocumentTypeCommand(
            "Акт", $"ACT_{Guid.NewGuid():N}"[..12], DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}""")));
        var construction = await m.Send(new CreateConstructionCommand("Стройка журнала", Guid.NewGuid()));
        var section = await m.Send(new CreateSectionCommand(construction.Id, "ЭОМ"));
        var set = await m.Send(new CreateDocumentSetCommand(section.Id, "250701.ЭОМ-1"));
        await m.Send(new AddDocumentToSetCommand(set.Id, type.Id));
        return set.Id;
    }

    private async Task<(HttpClient Client, Guid Id)> SignInAsync(string role, string displayName)
    {
        var email = $"mcpjournal_{Guid.NewGuid():N}@example.com";
        Guid id;
        using (var scope = fixture.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            id = user.Id;
        }

        var client = fixture.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, id);
    }
}
