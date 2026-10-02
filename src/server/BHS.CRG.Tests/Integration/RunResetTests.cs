using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Очистка базы раз за прогон (issue #1142): что она сносит, что обязана оставить и почему второй раз
/// за прогон не случается.
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

    /// <summary>
    /// Следы прошлого прогона уходят, а созданное стартом хоста остаётся. Вторая половина важнее
    /// первой: старт к моменту очистки уже прошёл, и снесённые системные роли, права или встроенные
    /// профили никто не вернул бы до конца прогона — падали бы входы и проверки прав во всех классах
    /// хоста, а причину искали бы в них.
    /// </summary>
    [Fact]
    public async Task Очистка_сносит_следы_тестов_и_оставляет_созданное_стартом()
    {
        await host.ResetForRunAsync();
        var seeded = await SeededAsync();
        // Сравнивать «до» и «после» имеет смысл, только когда есть что терять.
        Assert.All(seeded, s => Assert.True(s.Value > 0, $"{s.Key}: стартом не создано ничего"));

        var narrow = await LeaveTracesAsync();

        await host.ResetForRunAsync();

        Assert.Equal(0, await CountAsync("\"AspNetUsers\""));
        Assert.Equal(0, await CountAsync("\"AspNetUserRoles\""));
        Assert.Equal(0, await CountAsync("\"RefreshTokens\""));
        Assert.Equal(0, await CountAsync("constructions"));

        using var scope = host.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        Assert.Null(await roles.FindByNameAsync(narrow));
        Assert.Equal(seeded, await SeededAsync());
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
        await LeaveTracesAsync();
        var (users, sites) = (await CountAsync("\"AspNetUsers\""), await CountAsync("constructions"));
        Assert.True(users > 0 && sites > 0, "следы не оставлены — сносить было бы нечего");

        // Хост следующего класса: новый экземпляр на той же базе и тот же вызов, что делает xUnit.
        using var next = new ModulePortsHost();
        await next.InitializeAsync();
        await host.InitializeAsync();

        Assert.Equal(users, await CountAsync("\"AspNetUsers\""));
        Assert.Equal(sites, await CountAsync("constructions"));

        await host.ResetForRunAsync();
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

    /// <summary>Созданное стартом хоста: по числу строк на таблицу, системные роли — поимённо.</summary>
    private async Task<Dictionary<string, int>> SeededAsync()
    {
        var seeded = new Dictionary<string, int>
        {
            ["permissions"] = await CountAsync("permissions"),
            ["recognition_profiles"] = await CountAsync("recognition_profiles"),
            ["AspNetRoleClaims"] = await CountAsync("\"AspNetRoleClaims\""),
        };

        using var scope = host.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var definition in SystemRoles.All)
            seeded[$"роль {definition.Name}"] = await roles.FindByNameAsync(definition.Name) is null ? 0 : 1;
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
