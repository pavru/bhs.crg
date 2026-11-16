using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using BHS.CRG.Application.Activity;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Канал и автор едут в фоновую задачу (ТЗ CORE-28.1, issue #1336, design D3): задача, поставленная
/// запросом через MCP, пишет журнал от имени заказчика с каналом <c>mcp</c>, а не «Системой».
///
/// <para>Сквозной прогон через настоящие очередь и фоновый цикл: работа выполняется в своей области служб
/// вне запроса, и проверить окружение автора можно только там. Запрос — подставной контекст с меткой
/// адреса <c>/mcp</c>, той же, что ставит <c>MapMcp</c>: так проверяется и чтение метки автором журнала.</para>
/// </summary>
public partial class ModulePortsTests
{
    [Fact]
    public async Task Задача_поставленная_через_MCP_пишет_журнал_от_заказчика_с_каналом_mcp()
    {
        var probe = host.Services.GetRequiredService<ProbeModuleWork>();
        var accessor = host.Services.GetRequiredService<IHttpContextAccessor>();
        var user = await CreateUserAsync("Агент Иванова");
        try
        {
            using var scope = host.Services.CreateScope();
            var jobs = scope.ServiceProvider.GetRequiredService<IModuleJobs>();

            Guid viaMcp;
            accessor.HttpContext = McpRequest(user);
            try
            {
                viaMcp = await jobs.EnqueueAsync(ProbeModuleWork.Code, Guid.NewGuid(), "Проба через MCP");
            }
            finally
            {
                accessor.HttpContext = null;
            }

            await WaitForAsync(jobs, viaMcp, ModuleJobStatus.Succeeded);
            var author = probe.Authors[viaMcp];
            Assert.Equal(user.Id, author.Id);
            Assert.Equal("Агент Иванова", author.Name);
            Assert.Same(ActivityChannel.Mcp, author.Channel);

            using (var check = host.Services.CreateScope())
            {
                var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.Equal("mcp", await db.Jobs.Where(j => j.Id == viaMcp).Select(j => j.Channel).SingleAsync());
            }

            // Следующая задача — без заказчика: окружение прежней обязано было закрыться вместе с ней, иначе
            // плановая работа подписалась бы чужим человеком и каналом агента.
            var after = await jobs.EnqueueAsync(ProbeModuleWork.Code, Guid.NewGuid(), "Проба вне запроса");
            await WaitForAsync(jobs, after, ModuleJobStatus.Succeeded);
            Assert.Equal(ActivityActor.System, probe.Authors[after]);
        }
        finally
        {
            await DeleteUserAsync(user);
        }
    }

    /// <summary>Запрос через адрес MCP: вошедший пользователь и метка канала в метаданных адреса.</summary>
    private static DefaultHttpContext McpRequest(ApplicationUser user)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim("displayName", user.DisplayName),
            ], "Test")),
        };
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(ActivityChannel.Mcp), "mcp"));
        return context;
    }

    // Учётные записи фикстура не чистит — «чистят точечно те, кто их заводит» (FixtureResetCoverageTests).
    private async Task<ApplicationUser> CreateUserAsync(string displayName)
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"channel_{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, "Passw0rd!")).Succeeded);
        return user;
    }

    private async Task DeleteUserAsync(ApplicationUser user)
    {
        using var scope = host.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByIdAsync(user.Id.ToString()) is { } found)
            Assert.True((await users.DeleteAsync(found)).Succeeded);
    }
}
