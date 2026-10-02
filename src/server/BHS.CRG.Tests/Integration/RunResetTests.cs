using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Очистка базы раз за прогон (issue #1142): что она сносит, что возвращает старт, почему второй раз
/// за прогон не случается и почему второй ПРОГОН на той же базе не начинается.
///
/// <para>Проверяется на хосте портов, а не на хостах счетов, на которых накопление нашлось: его база
/// и так сбрасывается перед каждым тестом, то есть очистка посреди прогона здесь никому не мешает. У
/// хостов счетов посев статический, и лишняя очистка снесла бы его из-под соседних классов — ровно
/// то, от чего стоит второй сторож.</para>
/// </summary>
[Collection("Integration")]
public class RunResetTests(ModulePortsHost host) : IClassFixture<ModulePortsHost>
{
    private const string Password = "Test#12345";

    private string Database => host.Services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")!;

    /// <summary>
    /// Учётные таблицы сносятся целиком, а созданное стартом возвращает СТАРТ — поэтому очистка и
    /// стоит до него. Вторая половина важнее первой: вернись системные роли не все или без прав,
    /// падали бы входы и проверки прав во всех классах хоста, а причину искали бы в них.
    ///
    /// <para>⚠️ Второй хост здесь поднимается нарочно: проверяется именно то, что делает его старт.
    /// Сверяется при этом не список «что создаёт старт», а состояние до и после — список у очистки
    /// был, и расходился бы с приложением на первой роли, заведённой мимо него.</para>
    /// </summary>
    [Fact]
    public async Task Учётные_таблицы_сносятся_а_созданное_стартом_возвращает_старт()
    {
        var seeded = await SeededAsync(host);
        // Сравнивать «до» и «после» имеет смысл, только когда есть что терять.
        Assert.All(seeded, s => Assert.True(s.Value > 0, $"{s.Key}: стартом не создано ничего"));
        var narrow = await LeaveTracesAsync();

        await TestRunDatabase.ClearIdentityAsync(Database);

        Assert.Equal(0, await CountAsync("\"AspNetUsers\""));
        Assert.Equal(0, await CountAsync("\"AspNetRoles\""));
        Assert.Equal(0, await CountAsync("\"AspNetRoleClaims\""));
        Assert.Equal(0, await CountAsync("\"RefreshTokens\""));

        using var next = new ModulePortsHost();
        Assert.Equal(seeded, await SeededAsync(next));
        using var scope = next.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        Assert.Null(await roles.FindByNameAsync(narrow));

        await host.ResetDatabaseAsync();
    }

    /// <summary>
    /// Вторая очистка за прогон не случается — даже у ДРУГОГО экземпляра того же хоста. Экземпляр
    /// xUnit создаёт на каждый класс, а посев у классов хостов счетов статический: очистка «на
    /// экземпляр» сносила бы посев первого класса перед вторым, и падал бы второй.
    /// </summary>
    [Fact]
    public async Task Вторая_очистка_за_прогон_ничего_не_сносит()
    {
        // Своему хосту xUnit уже позвал InitializeAsync, и очистка этого прогона позади. Проверяем,
        // а не полагаемся: сними её кто-нибудь с InitializeAsync — базы снова начнут копить, и ни один
        // тест этого не заметит, потому что на маленькой базе всё зелёное.
        Assert.True(host.CleanedThisRun, "хост класса поднят, а его база перед первым тестом не очищена");
        var narrow = await LeaveTracesAsync();
        var (users, sites) = (await CountAsync("\"AspNetUsers\""), await CountAsync("constructions"));
        Assert.True(users > 0 && sites > 0, "следы не оставлены — сносить было бы нечего");

        // Хост следующего класса: новый экземпляр на той же базе. Поднимать его незачем — ключ ворот
        // берётся из объявленной базы, и до служб дело не доходит.
        using var next = new ModulePortsHost();
        await next.ResetOncePerRunAsync();
        await host.InitializeAsync();

        Assert.Equal(users, await CountAsync("\"AspNetUsers\""));
        Assert.Equal(sites, await CountAsync("constructions"));

        await RemoveTracesAsync(narrow);
    }

    /// <summary>
    /// Базу держит этот прогон, и второй претендент получает отказ с причиной — а не сносит строки
    /// из-под идущих тестов. Претендентом здесь служит новое подключение из этого же процесса: замок
    /// держится подключением, и для базы оно ничем не отличается от соседнего прогона.
    /// </summary>
    [Fact]
    public async Task Второй_прогон_на_занятой_базе_получает_отказ_с_причиной()
    {
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestRunDatabase.LockAsync(Database, TimeSpan.Zero));

        Assert.Contains("уже держит другой прогон", refusal.Message);
        Assert.Contains("BHS_TEST_DB", refusal.Message);
        // Отказ называет держателя — иначе искать висящий процесс пришлось бы перебором.
        Assert.Contains($"pid {Environment.ProcessId}", refusal.Message);
    }

    /// <summary>
    /// То, что оставляет после себя класс хоста счетов: вошедший пользователь с сессией, своя роль с
    /// одним правом и стройка. Возвращает имя роли.
    /// </summary>
    private async Task<string> LeaveTracesAsync()
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;

        var name = $"Narrow_{Guid.NewGuid():N}";
        var roles = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var role = new IdentityRole<Guid>(name);
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        Assert.True((await roles.AddClaimAsync(role, new Claim(RoleSynchronizer.PermissionClaim, "costs.waybill.read"))).Succeeded);

        var email = $"trace_{Guid.NewGuid():N}@test.local";
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        Assert.True((await users.AddToRoleAsync(user, name)).Succeeded);

        var db = services.GetRequiredService<AppDbContext>();
        db.Set<RefreshToken>().Add(new RefreshToken
        {
            Id = Guid.NewGuid(), UserId = user.Id, TokenHash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();

        await services.GetRequiredService<IMediator>().Send(new CreateConstructionCommand("След прошлого прогона", Guid.NewGuid()));
        return name;
    }

    /// <summary>
    /// Убрать следы поимённо. Общей очисткой здесь не обойтись: она сносит и системные роли, а
    /// возвращает их только старт.
    /// </summary>
    private async Task RemoveTracesAsync(string narrow)
    {
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM "RefreshTokens"; DELETE FROM "AspNetUsers";""");
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            Assert.True((await roles.DeleteAsync((await roles.FindByNameAsync(narrow))!)).Succeeded);
        }
        await host.ResetDatabaseAsync();
    }

    /// <summary>
    /// Созданное стартом хоста, как оно лежит в базе: число строк справочников и КАЖДАЯ роль со
    /// своим числом прав. Имена ролей берутся из базы, а не из кода приложения.
    /// </summary>
    private static async Task<Dictionary<string, int>> SeededAsync(IntegrationTestFixture fixture)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var seeded = new Dictionary<string, int>
        {
            ["permissions"] = await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM permissions""").SingleAsync(),
            ["recognition_profiles"] = await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM recognition_profiles""").SingleAsync(),
        };

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in await roles.Roles.ToListAsync())
            // +1 — сама роль: у «Администратора» права не перечислены, и ноль прав не должен читаться
            // как «роли нет».
            seeded[$"роль {role.Name}"] = 1 + (await roles.GetClaimsAsync(role)).Count;
        return seeded;
    }

    private async Task<int> CountAsync(string table)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
#pragma warning disable EF1002 // имя таблицы — константа этого файла, а не значение
        return await db.Database.SqlQueryRaw<int>($"SELECT count(*)::int AS \"Value\" FROM {table}").SingleAsync();
#pragma warning restore EF1002
    }
}
