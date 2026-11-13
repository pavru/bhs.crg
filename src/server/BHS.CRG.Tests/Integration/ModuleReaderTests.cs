using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Параметр доступа контракта из трёх источников (ТЗ CORE-34, AUTH-6; plan-22): запрос, фоновая операция
/// пользователя, система. На живом приложении: права считаются из базы тем же счётчиком, что у ворот, и
/// поддельный контейнер проверил бы только переходник, а не это.
/// </summary>
[Collection("Integration")]
public class ModuleReaderTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string Permission = "id.document.read";

    // Заведённое тестом убирается за собой: база коллекции общая, и чужие роли и учётные записи копились бы
    // в списках, которые проверяют соседние классы.
    private readonly List<Guid> _users = [];
    private readonly List<Guid> _roles = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var id in _users)
            if (await users.FindByIdAsync(id.ToString()) is { } user)
                await users.DeleteAsync(user);
        foreach (var id in _roles)
            if (await roles.FindByIdAsync(id.ToString()) is { } role)
                await roles.DeleteAsync(role);
    }

    [Fact]
    public async Task Запрос_даёт_права_пользователя_и_открытые_ему_модули()
    {
        var (userId, stamp) = await UserWithAsync(Permission);
        using var scope = fixture.Services.CreateScope();
        var http = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        http.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", userId.ToString()),
                    new Claim(JwtTokens.SecurityStampClaim, stamp),
                    new Claim(ClaimTypes.Name, "Иванов"),
                ], "test")),
        };
        try
        {
            var reader = await scope.ServiceProvider.GetRequiredService<IModuleReaders>().ForRequestAsync();

            Assert.False(reader.IsSystem);
            Assert.Equal(userId, reader.UserId);
            Assert.Equal("Иванов", reader.Who);
            Assert.True(reader.Allows(Permission));
            Assert.True(reader.Allows("id"));             // модуль открыт его правом — ключ модуля в наборе
            Assert.False(reader.Allows("id.config.edit"));
        }
        finally
        {
            http.HttpContext = null;
        }
    }

    [Fact]
    public async Task Фоновая_операция_идёт_в_правах_поставившего()
    {
        var (userId, _) = await UserWithAsync(Permission);
        using var scope = fixture.Services.CreateScope();

        var reader = await scope.ServiceProvider.GetRequiredService<IModuleReaders>().ForJobAsync(Run(userId));

        Assert.False(reader.IsSystem);
        Assert.Equal(userId, reader.UserId);
        Assert.True(reader.Allows(Permission));
        Assert.False(reader.Allows("id.config.edit"));
    }

    [Fact]
    public async Task Операция_удалённого_пользователя_идёт_от_системы_с_причиной()
    {
        using var scope = fixture.Services.CreateScope();
        var gone = Guid.NewGuid();

        var reader = await scope.ServiceProvider.GetRequiredService<IModuleReaders>().ForJobAsync(Run(gone));

        Assert.True(reader.IsSystem);
        Assert.Empty(reader.Keys);
        Assert.Contains(gone.ToString(), reader.SystemReason);
    }

    /// <summary>
    /// Задача без владельца (поставлена вне запроса: расписание, старт) — система с честной причиной, а не
    /// «учётная запись 00000000-… удалена»: удалять было некого (ревью PR #1957).
    /// </summary>
    [Fact]
    public async Task Операция_без_владельца_идёт_от_системы_а_не_от_удалённого_автора()
    {
        using var scope = fixture.Services.CreateScope();

        var reader = await scope.ServiceProvider.GetRequiredService<IModuleReaders>().ForJobAsync(Run(Guid.Empty));

        Assert.True(reader.IsSystem);
        Assert.Empty(reader.Keys);
        Assert.Contains("не человек", reader.SystemReason);
        Assert.DoesNotContain("удалена", reader.SystemReason);
    }

    [Fact]
    public void Система_без_человека_несёт_причину_и_не_несёт_ключей()
    {
        using var scope = fixture.Services.CreateScope();
        var readers = scope.ServiceProvider.GetRequiredService<IModuleReaders>();

        var reader = readers.ForSystem("ночной пересчёт сводки");

        Assert.True(reader.IsSystem);
        Assert.Null(reader.UserId);
        Assert.Empty(reader.Keys);
        Assert.Equal("ночной пересчёт сводки", reader.SystemReason);
        Assert.Throws<ArgumentException>(() => readers.ForSystem(" "));
    }

    [Fact]
    public async Task Вне_запроса_параметр_запроса_дефект_вызывающего()
    {
        using var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IModuleReaders>().ForRequestAsync());
    }

    private static ModuleJobRun Run(Guid userId) =>
        new(Guid.NewGuid(), Guid.Empty, userId, null, (_, _, _) => Task.CompletedTask);

    /// <summary>Пользователь с ролью из этих прав — и его штамп: без штампа счётчик прав токену не верит.</summary>
    private async Task<(Guid Id, string Stamp)> UserWithAsync(params string[] permissions)
    {
        using var scope = fixture.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var role = new IdentityRole<Guid>($"Reader_{Guid.NewGuid():N}");
        Assert.True((await roles.CreateAsync(role)).Succeeded);
        _roles.Add(role.Id);
        foreach (var code in permissions)
            Assert.True((await roles.AddClaimAsync(role, new Claim(RoleSynchronizer.PermissionClaim, code))).Succeeded);

        var email = $"reader_{Guid.NewGuid():N}@test.local";
        var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
        Assert.True((await users.CreateAsync(user, "Passw0rd!Reader")).Succeeded);
        _users.Add(user.Id);
        Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
        return (user.Id, await users.GetSecurityStampAsync(user));
    }
}
