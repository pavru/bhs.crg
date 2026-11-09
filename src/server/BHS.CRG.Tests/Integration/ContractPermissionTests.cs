using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Activity;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Notifications;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// «Право контракта» на живом хосте (change <c>plan-21-contract-permissions</c>, ТЗ AUTH-8.1, PLAN-23):
/// право, которое модуль выдаёт другим, не открывает сам модуль — ни ворота его группы, ни
/// <c>available</c> в <c>/api/account/access</c>, ни его уведомления.
///
/// <para><b>Почему настоящий модуль с подменённым признаком, а не пробный модуль.</b> Состав поставки
/// назван в одном месте (<c>DeliveredModules</c>), и вставить в живой хост пробный модуль можно только
/// дверью в корне композиции, которой у поставки быть не должно. Ни одного права контракта сегодня не
/// объявлено (их объявят <c>plan-23</c>, <c>plan-24</c>), поэтому хост берёт справочник прав поставки и
/// помечает правом контракта одно право модуля <c>costs</c> — <see cref="Contract" />. Всё остальное —
/// ворота, ответ доступа, аудитория уведомлений — настоящее и спрашивает тот же справочник из DI.
/// Ворота <c>perm:</c> вне группы и вид отказа при старте проверяет
/// <c>ContractPermissionGateTests</c> на лёгком хосте с пробным модулем.</para>
///
/// <para>Ломается так: верните в <see cref="ModuleAccess.IsOpen" /> проверку по одному началу кода —
/// краснеют первый, третий и четвёртый сценарии.</para>
/// </summary>
[Collection(ContractPermissionCollection.Name)]
public class ContractPermissionTests(ContractPermissionHost host)
{
    /// <summary>Право, помеченное хостом правом контракта. Стоит и на адресе группы — <see cref="Route" />.</summary>
    internal const string Contract = "costs.report.read";

    /// <summary>Обычное право того же модуля.</summary>
    private const string Ordinary = "costs.invoice.read";

    /// <summary>Адрес группы <c>costs</c> под <c>perm:</c> на праве контракта — его закрывают ворота модуля.</summary>
    private const string Route = "/api/costs/site-costs";

    private const string Password = "Passw0rd!Contract";

    [Fact]
    public void Хост_пометил_право_контракта_и_только_его()
    {
        var catalog = host.Services.GetRequiredService<PermissionCatalog>();

        Assert.True(catalog.IsContract(Contract));
        Assert.False(catalog.IsContract(Ordinary));
    }

    [Fact]
    public async Task Только_право_контракта_модуль_не_открывает()
    {
        var (client, _) = await SignInWithAsync(Contract);

        var module = Assert.Single((await AccessProbe.AccessAsync(client)).Modules, m => m.Code == "costs");
        Assert.False(module.Available);
        // Право на адресе есть, а пройти нельзя: адрес стоит в группе модуля, и ворота модуля впереди.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task Право_контракта_вместе_с_обычным_модуль_открывает()
    {
        var (client, _) = await SignInWithAsync(Contract, Ordinary);

        var module = Assert.Single((await AccessProbe.AccessAsync(client)).Modules, m => m.Code == "costs");
        Assert.True(module.Available);
        // Тот же адрес теперь открыт: модуль открыло обычное право, а сам адрес — право контракта.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Route)).StatusCode);
    }

    [Fact]
    public async Task Уведомление_модуля_не_видит_носитель_одного_права_контракта()
    {
        var (_, contractOnly) = await SignInWithAsync(Contract);
        var (_, ordinary) = await SignInWithAsync(Ordinary);

        var id = await PublishAsync($"Дела модуля счетов {Guid.NewGuid():N}", "costs");

        // Второе — проверка самой проверки: без него отказ мог бы значить «уведомление не дошло никому».
        Assert.False(await SeesAsync(contractOnly, id));
        Assert.True(await SeesAsync(ordinary, id));
    }

    [Fact]
    public async Task Редактор_ролей_отдаёт_признак_права()
    {
        var (admin, _) = await SignInWithAsync(CorePermissions.UsersManage);

        var groups = await admin.GetFromJsonAsync<JsonElement>("/api/roles/permissions");
        var costs = groups.EnumerateArray().Single(g => g.GetProperty("module").GetString() == "costs");
        bool ContractOf(string code) => costs.GetProperty("permissions").EnumerateArray()
            .Single(p => p.GetProperty("code").GetString() == code).GetProperty("contract").GetBoolean();

        Assert.True(ContractOf(Contract));
        Assert.False(ContractOf(Ordinary));
    }

    /// <summary>
    /// Журнал действий модуля (AUTH-8.1): записи <c>costs</c> носителю одного права контракта не видны,
    /// с обычным правом — видны. Действие — без своего права чтения: его видит всякий, кому открыт модуль,
    /// и закрыть его может только правило «модуль открыт». Действие со своим правом чтения сторожем не
    /// годится — его закрыло бы и отсутствие того права (проверено: с ним тест зеленел на префиксе).
    /// </summary>
    [Fact]
    public void Журнал_модуля_не_открывает_право_контракта()
    {
        var catalog = host.Services.GetRequiredService<ActivityActionCatalog>();
        const string action = "costs.article.created";
        Assert.True(catalog.Declares(action));

        Assert.False(catalog.VisibleTo([Contract]).Shows(action));
        Assert.True(catalog.VisibleTo([Contract, Ordinary]).Shows(action));
    }

    /// <summary>
    /// Параметр доступа наборов (AUTH-8.1, CORE-24.1): ключ модуля носителю одного права контракта не
    /// выдаётся — опубликованные наборы <c>costs</c> ему закрыты; с обычным правом ключ есть.
    /// </summary>
    [Fact]
    public async Task Параметр_доступа_не_даёт_ключа_модуля_за_право_контракта()
    {
        var (_, contractOnly) = await SignInWithAsync(Contract);
        var (_, ordinary) = await SignInWithAsync(Contract, Ordinary);
        var resolver = host.Services.GetRequiredService<IDataAccessResolver>();

        var closed = await resolver.ForUserAsync(contractOnly, CancellationToken.None);
        var open = await resolver.ForUserAsync(ordinary, CancellationToken.None);

        Assert.False(closed.Allows("costs"));
        Assert.True(closed.Allows(Contract));   // само право в параметре есть — оно для «perm:»-ключей
        Assert.True(open.Allows("costs"));
    }

    /// <summary>«Мои права» называют признак тому, у кого право есть: отсюда человек узнаёт, почему раздела нет.</summary>
    [Fact]
    public async Task Мои_права_отдают_признак_права_контракта()
    {
        var (client, _) = await SignInWithAsync(Contract);

        var groups = await client.GetFromJsonAsync<JsonElement>("/api/account/permissions");
        var mine = groups.EnumerateArray().SelectMany(g => g.GetProperty("permissions").EnumerateArray())
            .Single(p => p.GetProperty("code").GetString() == Contract);

        Assert.True(mine.GetProperty("contract").GetBoolean());
    }

    private async Task<(HttpClient Client, Guid UserId)> SignInWithAsync(params string[] permissions)
    {
        var roleName = $"Contract_{Guid.NewGuid():N}";
        var email = $"contract_{Guid.NewGuid():N}@test.local";

        var client = host.CreateClient();
        Guid userId;
        using (var scope = host.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var role = new IdentityRole<Guid>(roleName);
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            foreach (var code in permissions)
                Assert.True((await roles.AddClaimAsync(
                    role, new Claim(RoleSynchronizer.PermissionClaim, code))).Succeeded);

            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = "Тест", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, roleName)).Succeeded);
            userId = user.Id;
        }

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, userId);
    }

    private async Task<Guid> PublishAsync(string title, string audience)
    {
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<INotificationService>()
            .PublishAsync(NotificationSeverity.Info, title, "текст", "Тест", audience: audience);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Notifications.Where(n => n.Title == title).Select(n => n.Id).SingleAsync();
    }

    private async Task<bool> SeesAsync(Guid userId, Guid notificationId)
    {
        using var scope = host.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<INotificationService>()
            .GetAsync(userId, take: 300)).Any(n => n.Id == notificationId);
    }
}

[CollectionDefinition(Name)]
public sealed class ContractPermissionCollection : ICollectionFixture<ContractPermissionHost>
{
    public const string Name = "Хост «право контракта»";
}

/// <summary>
/// Хост «только счета» со своей базой «…_contract», у которого право <see cref="ContractPermissionTests.Contract" />
/// помечено правом контракта. Своя база — по той же причине, что у <see cref="CostsOnlyHost" />:
/// состав системных ролей приводится при старте к объявленному, и общий хост с другим справочником
/// менял бы права ролям соседних классов.
/// </summary>
public sealed class ContractPermissionHost : ModuleOnlyHost
{
    protected override string ModuleCode => "costs";

    protected override string DatabaseSuffix => "contract";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Справочник поставки — тот же, только у одного права поднят признак. ConfigureTestServices,
        // а не ConfigureServices: подмена обязана лечь ПОСЛЕ регистрации модулей приложением.
        builder.ConfigureTestServices(services =>
        {
            var registered = services.Single(d => d.ServiceType == typeof(PermissionCatalog));
            var delivered = (PermissionCatalog)registered.ImplementationInstance!;
            services.Remove(registered);
            services.AddSingleton(new PermissionCatalog(
            [
                .. delivered.All.Select(p =>
                    // Пометка «читать всё» — вместе с признаком: право контракта входящим быть не может
                    // (D12), и справочник отверг бы его при сборке.
                    p.Code == ContractPermissionTests.Contract
                        ? p with { Contract = true, ReadAll = ReadAllMark.Out("выдача другому модулю") }
                        : p),
            ]));
        });
    }
}
