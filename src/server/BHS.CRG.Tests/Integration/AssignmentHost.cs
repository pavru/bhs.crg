using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Хост назначений (change <c>work-11-assignments</c>): только модуль <c>work</c>, своя база и часы ПОРТА,
/// прибитые к 20:00 31.10.2026 по Москве — во Владивостоке в этот миг уже 01.11. Заменяются часы порта, а не
/// приложения: по часам приложения живёт срок входа (как у <see cref="InvoiceClockHost" />).
/// </summary>
public sealed class AssignmentHost : ModuleOnlyHost
{
    /// <summary>
    /// Коллекция классов назначений: хост у них ОДИН и база одна, поэтому классы идут друг за другом. Хост на
    /// класс (<c>IClassFixture</c>) дал бы три хоста на одной базе — и параллельные классы чистили бы её друг у
    /// друга посреди теста.
    /// </summary>
    public const string Collection = "Assignments";

    /// <summary>20:00 31.10.2026 по Москве, 03:00 01.11.2026 во Владивостоке.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 31, 17, 0, 0, TimeSpan.Zero);

    protected override string ModuleCode => WorkModule.ModuleCode;

    protected override string DatabaseSuffix => "assign";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IModuleClock>();
            services.AddScoped<IModuleClock>(sp => new ModuleClockPort(
                sp.GetRequiredService<IAppSettingsStore>(), new Fixed(Now),
                sp.GetRequiredService<IRepository<Construction>>()));
        });
    }

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>
/// Мир назначений: справочник сотрудников, стройки, учётные записи с правами и связи — вымышленные. Назначения
/// для подготовки кладутся мимо адресов, прямо в контекст модуля: подготовка не должна зависеть от того, что
/// проверяется.
/// </summary>
internal sealed class AssignmentWorld(AssignmentHost host)
{
    private const string Password = "Passw0rd!Assign";

    public static DateOnly Day(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Чистая база, пояс компании — Москва.</summary>
    public async Task ResetAsync()
    {
        await host.ResetDatabaseAsync();
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>()
            .SetAsync(AppSettingKeys.CompanyTimeZone, "Europe/Moscow");
    }

    /// <summary>«Персона» и справочник «Сотрудник» от объявления ядра.</summary>
    public async Task<Guid> DirectoryAsync()
    {
        _ = host.CreateClient();
        using (var scope = host.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            await repo.AddAsync(DocumentType.Create("Лицо", CoreRecordTypes.PersonCode, DocumentTypeKind.Composite, null,
                JsonDocument.Parse("""{"fields":[]}"""), TypeOwner.Core, TypeVisibility.Shared));
            await repo.SaveChangesAsync();
        }
        return (await SendAsync(new ProjectModuleTypeCommand(CoreRecordTypes.Employee)))!.Id;
    }

    public async Task<Guid> EmployeeAsync(Guid typeId, string name, string? hired = null, string? dismissed = null)
    {
        var data = new Dictionary<string, object> { ["ТабельныйНомер"] = Guid.NewGuid().ToString("N")[..8] };
        if (hired is not null) data["ПринятС"] = hired;
        if (dismissed is not null) data["УволенС"] = dismissed;
        return (await SendAsync(new CreateCommonDataEntryCommand(name, typeId,
            JsonDocument.Parse(JsonSerializer.Serialize(data)), CatalogScope.System, null, CreateAnyway: true))).Id;
    }

    public Task ArchiveAsync(Guid employeeId) => SendAsync(new SetRecordArchiveCommand(employeeId, true));

    public async Task<Guid> SiteAsync(string name, string? zone = null)
    {
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Construction>>();
        var site = Construction.Create(name, Guid.NewGuid());
        if (zone is not null) site.SetTimeZone(zone);
        await repo.AddAsync(site);
        await repo.SaveChangesAsync();
        return site.Id;
    }

    /// <summary>Учётная запись ровно с этими правами и клиент с её токеном.</summary>
    public async Task<(Guid UserId, HttpClient Client)> UserAsync(string displayName, params string[] permissions)
    {
        var client = host.CreateClient();
        var email = $"assign_{Guid.NewGuid():N}@test.local";
        Guid userId;
        using (var scope = host.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var role = new IdentityRole<Guid>($"Assign_{Guid.NewGuid():N}");
            Assert.True((await roles.CreateAsync(role)).Succeeded);
            foreach (var code in permissions)
                Assert.True((await roles.AddClaimAsync(role, new Claim(RoleSynchronizer.PermissionClaim, code))).Succeeded);
            var user = new ApplicationUser { UserName = email, Email = email, DisplayName = displayName, EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role.Name!)).Succeeded);
            userId = user.Id;
        }
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (userId, client);
    }

    /// <summary>Менеджер с правом назначать, связанный с сотрудником.</summary>
    public async Task<(Guid UserId, HttpClient Client)> ManagerAsync(Guid employeeId, params string[] extra)
    {
        var (userId, client) = await UserAsync("Менеджер", [WorkModule.Assign, .. extra]);
        await LinkAsync(employeeId, userId);
        return (userId, client);
    }

    public async Task LinkAsync(Guid employeeId, Guid userId)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.EmployeeAccounts.Add(EmployeeAccount.Link(employeeId, userId, "Связанная запись"));
        await db.SaveChangesAsync();
    }

    /// <summary>Назначение мимо адресов — для подготовки.</summary>
    public async Task<Guid> AssignedAsync(Guid employeeId, Guid siteId, AssignmentKind kind, string from, string? to)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        var assignment = Assignment.Create(employeeId, siteId, kind, Day(from), to is null ? null : Day(to), DateTimeOffset.UtcNow);
        db.Assignments.Add(assignment);
        await db.SaveChangesAsync();
        return assignment.Id;
    }

    public async Task<int> CountAsync(Guid siteId)
    {
        using var scope = host.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<WorkDbContext>().Assignments.Count(a => a.ConstructionId == siteId);
    }

    public async Task<T> SendAsync<T>(IRequest<T> request)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
    }

    public static object Body(Guid employee, Guid site, string kind, string from, string? to) =>
        new { employeeId = employee, constructionId = site, kind, dateFrom = from, dateTo = to };

    public static HttpRequestMessage Versioned(HttpMethod method, string url, string? version, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return request;
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        (await JsonAsync(response)).GetProperty("error").GetString()!;
}

/// <summary>Общий хост классов назначений (см. <see cref="AssignmentHost.Collection" />).</summary>
[CollectionDefinition(AssignmentHost.Collection)]
public sealed class AssignmentCollection : ICollectionFixture<AssignmentHost>;
