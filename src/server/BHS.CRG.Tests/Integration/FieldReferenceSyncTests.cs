using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Settings;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Devices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Integration.AssignmentWorld;

namespace BHS.CRG.Tests.Integration;

[Collection(AssignmentHost.Collection)]
public sealed class FieldReferenceSyncTests(AssignmentHost host)
{
    private const string References = "/api/field/v1/references/";
    private const string Observation = "/api/field/v1/device/observation";
    private const string Date = "2026-11-01";
    private readonly AssignmentWorld _world = new(host);

    [Fact]
    public async Task Material_pages_reuse_the_snapshot_until_a_committed_catalog_change()
    {
        await _world.ResetAsync();
        var me = await _world.EmployeeAsync(await _world.DirectoryAsync(), "Монтажник");
        var seed = new WorkTypeSeed(host);
        var type = await seed.TypeAsync(TestDirectories.NomenclatureCode, "Номенклатура");
        using var scope = host.Services.CreateScope();
        await TestDirectories.AssignRolesAsync(scope.ServiceProvider);
        var cable = await seed.RecordAsync(type.Id, "Кабель");
        var tray = await seed.RecordAsync(type.Id, "Лоток");
        using var phone = host.CreateClient();
        await JoinAsync(phone, me);

        const string path = "materials?limit=1";
        var reads = host.ReferenceQueries.CatalogReads;
        var first = await PageAsync(phone, path);
        Assert.Equal(++reads, host.ReferenceQueries.CatalogReads);
        Assert.True(first.GetProperty("more").GetBoolean());

        await _world.SiteAsync("Новая стройка");
        var second = await PageAsync(phone, path, Cursor(first));
        Assert.False(second.GetProperty("replace").GetBoolean());
        Assert.Equal(new[] { cable.Id, tray.Id }.Order(), Ids(first).Concat(Ids(second)).Order());
        Assert.Empty(Ids(await PageAsync(phone, path, Cursor(second))));
        Assert.Equal(reads, host.ReferenceQueries.CatalogReads);

        var core = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using (var transaction = await core.Database.BeginTransactionAsync())
        {
            await core.DomainObjects.Where(x => x.Id == cable.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.ArchivedAt, AssignmentHost.Now));
            var unchanged = await PageAsync(phone, path, Cursor(first));
            Assert.False(unchanged.GetProperty("replace").GetBoolean());
            Assert.Equal(reads, host.ReferenceQueries.CatalogReads);
            await transaction.CommitAsync();
        }
        var updated = await PageAsync(phone, path, Cursor(first));
        Assert.True(updated.GetProperty("replace").GetBoolean());
        Assert.Equal([tray.Id], Ids(updated));
        Assert.Equal(++reads, host.ReferenceQueries.CatalogReads);
    }

    [Fact]
    public async Task Concurrent_change_replaces_incomplete_snapshot_and_other_cursor_still_continues()
    {
        await _world.ResetAsync();
        var directory = await _world.DirectoryAsync();
        var me = await _world.EmployeeAsync(directory, "Иванов");
        var peer = await _world.EmployeeAsync(directory, "Петров");
        var stranger = await _world.EmployeeAsync(directory, "Сидоров");
        var first = await _world.SiteAsync("Первая", "Asia/Vladivostok");
        var second = await _world.SiteAsync("Вторая");
        var foreign = await _world.SiteAsync("Чужая");
        var section = await _world.SendAsync(new CreateSectionCommand(first, "Старое название"));
        var hiddenSection = await _world.SendAsync(new CreateSectionCommand(second, "Исчезающий раздел"));
        await AssignAsync(me, first);
        var removed = await AssignAsync(me, second);
        await AssignAsync(peer, first);
        await AssignAsync(stranger, foreign);
        using var phone = host.CreateClient();
        using var other = host.CreateClient();
        await JoinAsync(phone, me);
        await JoinAsync(other, stranger);

        var sites = await PageAsync(phone, $"sites?date={Date}&limit=1");
        Assert.True(sites.GetProperty("more").GetBoolean());
        var sectionsPath = $"sections?date={Date}&limit=1";
        var sections = await PageAsync(phone, sectionsPath);
        Assert.True(sections.GetProperty("more").GetBoolean());
        var unfetched = Assert.Single(new[] { section.Id, hiddenSection.Id }.Except(Ids(sections)));
        await _world.SendAsync(new RenameSectionCommand(unfetched, "Между страницами"));
        var restarted = await PageAsync(phone, sectionsPath, Cursor(sections));
        Assert.True(restarted.GetProperty("replace").GetBoolean());
        var nextSection = await PageAsync(phone, sectionsPath, Cursor(restarted));
        Assert.False(nextSection.GetProperty("replace").GetBoolean());
        Assert.False(nextSection.GetProperty("more").GetBoolean());
        Assert.Equal(new[] { section.Id, hiddenSection.Id }.Order(), Ids(restarted).Concat(Ids(nextSection)).Order());
        var renamed = Assert.Single(restarted.GetProperty("items").EnumerateArray()
            .Concat(nextSection.GetProperty("items").EnumerateArray()), item => item.GetProperty("id").GetGuid() == unfetched);
        Assert.Equal("Между страницами", renamed.GetProperty("name").GetString());
        var peoplePath = $"participants?date={Date}&siteId={first}&limit=1";
        var people = await PageAsync(phone, peoplePath);
        var person = Assert.Single(people.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "fullName", "id" }, person.EnumerateObject().Select(p => p.Name).Order());
        Assert.True(people.GetProperty("more").GetBoolean());
        Assert.Empty(Ids(await PageAsync(other, peoplePath)));
        Assert.Equal([foreign], Ids(await PageAsync(other, $"sites?date={Date}")));
        await _world.SendAsync(new RenameSectionCommand(section.Id, "Новое название"));

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
            var assignment = await db.Assignments.SingleAsync(a => a.Id == removed);
            assignment.ChangePeriod(Day("2026-10-30"), Day("2026-10-31"), AssignmentHost.Now);
            await db.SaveChangesAsync();
        }
        var replacement = await PageAsync(phone, $"sites?date={Date}&limit=1", Cursor(sites));
        Assert.True(replacement.GetProperty("replace").GetBoolean());
        Assert.False(replacement.GetProperty("more").GetBoolean());
        Assert.Equal([first], Ids(replacement));
        Assert.Equal("Asia/Vladivostok", Assert.Single(replacement.GetProperty("items").EnumerateArray())
            .GetProperty("timeZoneId").GetString());
        var currentSections = await PageAsync(phone, sectionsPath, Cursor(nextSection));
        Assert.True(currentSections.GetProperty("replace").GetBoolean());
        Assert.False(currentSections.GetProperty("more").GetBoolean());
        Assert.Equal([section.Id], Ids(currentSections));
        Assert.Equal("Новое название", Assert.Single(currentSections.GetProperty("items").EnumerateArray())
            .GetProperty("name").GetString());

        var continuation = await PageAsync(phone, peoplePath, Cursor(people));
        Assert.False(continuation.GetProperty("replace").GetBoolean());
        Assert.Equal(new[] { me, peer }.Order(), Ids(people).Concat(Ids(continuation)).Order());

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
            await db.Assignments.Where(a => a.EmployeeId == me && a.ConstructionId == first).ExecuteDeleteAsync();
        }
        var scopeLost = await PageAsync(phone, peoplePath, Cursor(continuation));
        Assert.True(scopeLost.GetProperty("replace").GetBoolean());
        Assert.Equal("not-assigned", scopeLost.GetProperty("state").GetString());
        Assert.Empty(Ids(scopeLost));
        Assert.Equal([foreign], Ids(await PageAsync(other, $"sites?date={Date}")));
    }

    [Fact]
    public async Task Archived_variant_replaces_classifier_and_preserves_its_identity()
    {
        await _world.ResetAsync();
        var employee = await _world.EmployeeAsync(await _world.DirectoryAsync(), "Монтажник");
        var seed = new WorkVariantSeed(host);
        var (variantType, cable, _) = await seed.SetUpAsync();
        var variant = await seed.VariantAsync(variantType, cable, "в трубе");
        using var phone = host.CreateClient();
        await JoinAsync(phone, employee);

        var initial = await PageAsync(phone, "classifier");
        Assert.False(Variant(initial).GetProperty("archived").GetBoolean());
        await seed.Types.ArchiveAsync(variant.Id);
        var updated = await PageAsync(phone, "classifier", Cursor(initial));
        Assert.True(updated.GetProperty("replace").GetBoolean());
        Assert.True(Variant(updated).GetProperty("archived").GetBoolean());
        Assert.Empty(Ids(await PageAsync(phone, "classifier", Cursor(updated))));

        JsonElement Variant(JsonElement page)
        {
            var work = Assert.Single(page.GetProperty("items").EnumerateArray(), row => row.GetProperty("id").GetGuid() == cable.Id);
            var item = Assert.Single(work.GetProperty("variants").EnumerateArray());
            Assert.Equal(variant.Id, item.GetProperty("id").GetGuid());
            Assert.Equal(cable.Id, item.GetProperty("workTypeId").GetGuid());
            return item;
        }
    }

    [Fact]
    public async Task Work_only_explains_missing_hint_sources_and_resolves_line_position()
    {
        var world = new WorkPeriodWorld(host).World;
        await world.SeedAsync();
        var employee = await world.EmployeeAsync("Монтажник");
        await AssignAsync(employee, world.SiteA);
        using var phone = host.CreateClient();
        await JoinAsync(phone, employee);
        Assert.NotEmpty(Ids(await PageAsync(phone, "classifier")));
        foreach (var kind in new[] { "issued-materials", "estimated-materials" })
        {
            var page = await PageAsync(phone, $"{kind}?siteId={world.SiteA}&date={Date}");
            Assert.Equal("not-connected", page.GetProperty("state").GetString());
            Assert.Empty(Ids(page));
        }

        var path = $"positions?date={Date}";
        var before = await PageAsync(phone, path);
        using var created = await new FieldReportClient(phone).CreateAsync(
            world.Draft(Guid.NewGuid(), world.SiteA, date: Date, changeId: Guid.NewGuid()));
        var report = await FieldReportClient.ReadAsync(created, HttpStatusCode.Created);
        var line = Assert.Single(report.GetProperty("lines").EnumerateArray());
        var resolved = line.GetProperty("workPlanItemId").GetGuid();
        var positions = await PageAsync(phone, path, Cursor(before));
        Assert.True(positions.GetProperty("replace").GetBoolean());
        Assert.Contains(resolved, Ids(positions));
    }

    [Fact]
    public async Task Expired_cursor_replaces_snapshot_and_company_zone_is_available_without_assignments()
    {
        await _world.ResetAsync();
        var me = await _world.EmployeeAsync(await _world.DirectoryAsync(), "Монтажник");
        using var phone = host.CreateClient();
        await JoinAsync(phone, me);
        var path = $"sites?date={Date}";
        var initial = await PageAsync(phone, path);
        Assert.Empty(Ids(initial));
        Assert.Equal("Europe/Moscow", Zone(initial));
        Assert.False((await PageAsync(phone, path, Cursor(initial))).GetProperty("replace").GetBoolean());

        using var scope = host.Services.CreateScope();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("BHS.CRG.Field.ReferencePage.v1").ToTimeLimitedDataProtector();
        var expired = protector.Protect(protector.Unprotect(Cursor(initial)), DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.True((await PageAsync(phone, path, expired)).GetProperty("replace").GetBoolean());
        var inheritedSite = await _world.SiteAsync("Наследует пояс компании");
        await AssignAsync(me, inheritedSite);
        var assigned = await PageAsync(phone, path, Cursor(initial));
        Assert.Equal("Europe/Moscow", Assert.Single(assigned.GetProperty("items").EnumerateArray())
            .GetProperty("timeZoneId").GetString());
        await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>()
            .SetAsync(AppSettingKeys.CompanyTimeZone, "Asia/Vladivostok");

        var updated = await PageAsync(phone, path, Cursor(assigned));
        Assert.True(updated.GetProperty("replace").GetBoolean());
        Assert.Equal("Asia/Vladivostok", Zone(updated));
        Assert.Equal("Asia/Vladivostok", Assert.Single(updated.GetProperty("items").EnumerateArray())
            .GetProperty("timeZoneId").GetString());
    }

    [Fact]
    public async Task Site_address_uses_tagged_profile_value_and_never_returns_other_profile_fields()
    {
        await _world.ResetAsync();
        var me = await _world.EmployeeAsync(await _world.DirectoryAsync(), "Монтажник");
        var site = await _world.SiteAsync("Назначенная");
        var foreign = await _world.SiteAsync("Чужая");
        await AssignAsync(me, site);
        using var phone = host.CreateClient();
        await JoinAsync(phone, me);
        var path = $"sites?date={Date}";
        var initial = await PageAsync(phone, path);
        Assert.Equal(JsonValueKind.Null, Site(initial).GetProperty("address").ValueKind);

        var seed = new WorkTypeSeed(host);
        var parent = await seed.TypeAsync("construction-profile-base", "Базовые поля", Schema("postal", tagged: true));
        var type = await seed.TypeAsync("construction-profile", "Профиль стройки", """{"tags":["profile.construction"],"fields":[]}""",
            parentId: parent.Id);
        using var scope = host.Services.CreateScope();
        var core = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var template = DomainObject.Create(type.Id, null, JsonDocument.Parse("""{"postal":"Центральная, 1"}"""),
            CatalogScope.System, null);
        var profile = DomainObject.Create(type.Id, null, JsonDocument.Parse(
            $$"""{"_baseRef":"{{template.Id}}","internalNote":"Служебный реквизит"}"""), CatalogScope.Construction, site);
        var foreignProfile = DomainObject.Create(type.Id, null, JsonDocument.Parse("""{"postal":"Чужой адрес"}"""),
            CatalogScope.Construction, foreign);
        core.AddRange(template, profile, foreignProfile);
        (await core.Constructions.SingleAsync(c => c.Id == site)).SetProfileObject(profile.Id);
        (await core.Constructions.SingleAsync(c => c.Id == foreign)).SetProfileObject(foreignProfile.Id);
        await core.SaveChangesAsync();

        var inherited = await PageAsync(phone, path, Cursor(initial));
        Assert.Equal("Центральная, 1", Site(inherited).GetProperty("address").GetString());
        Assert.Equal(["address", "id", "name", "timeZoneId"], Site(inherited).EnumerateObject().Select(p => p.Name).Order());

        var baseType = await core.DocumentTypes.SingleAsync(t => t.Id == parent.Id);
        baseType.UpdateSchema(JsonDocument.Parse(Schema("renamed", tagged: true)));
        profile.SetData(JsonDocument.Parse("""{"renamed":" Полевая, 2 ","internalNote":"Служебный реквизит"}"""));
        await core.SaveChangesAsync();
        var changed = await PageAsync(phone, path, Cursor(inherited));
        Assert.True(changed.GetProperty("replace").GetBoolean());
        Assert.Equal("Полевая, 2", Site(changed).GetProperty("address").GetString());

        profile.SetData(JsonDocument.Parse("""{"renamed":"  ","internalNote":"Служебный реквизит"}"""));
        await core.SaveChangesAsync();
        Assert.Equal(JsonValueKind.Null, Site(await PageAsync(phone, path, Cursor(changed))).GetProperty("address").ValueKind);

        baseType.UpdateSchema(JsonDocument.Parse(Schema("renamed", tagged: false)));
        profile.SetData(JsonDocument.Parse("""{"renamed":"Полевая, 2","internalNote":"Служебный реквизит"}"""));
        await core.SaveChangesAsync();
        Assert.Equal(JsonValueKind.Null, Site(await PageAsync(phone, path)).GetProperty("address").ValueKind);
        await core.Entry(profile).ReloadAsync();
        Assert.Equal("Служебный реквизит", profile.Data.RootElement.GetProperty("internalNote").GetString());

        baseType.UpdateSchema(JsonDocument.Parse(Schema("renamed", tagged: true)));
        await core.SaveChangesAsync();
        var active = await PageAsync(phone, path);
        Assert.Equal("Полевая, 2", Site(active).GetProperty("address").GetString());
        // Штатная дверь не архивирует действующий профиль. Проверяем старую ссылку с архивным профилем.
        await core.DomainObjects.Where(p => p.Id == profile.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.ArchivedAt, AssignmentHost.Now));
        Assert.Equal(JsonValueKind.Null, Site(await PageAsync(phone, path, Cursor(active))).GetProperty("address").ValueKind);

        JsonElement Site(JsonElement page)
        {
            var item = Assert.Single(page.GetProperty("items").EnumerateArray());
            Assert.Equal(site, item.GetProperty("id").GetGuid());
            return item;
        }

        static string Schema(string key, bool tagged) => JsonSerializer.Serialize(new
        {
            fields = new[] { new { key, title = "Адрес", type = "string", tags = tagged ? new[] { "construction.address" } : [] } },
        });
    }

    [Fact]
    public async Task Own_observation_replaces_two_with_zero_without_writing_reports()
    {
        await _world.ResetAsync();
        var me = await _world.EmployeeAsync(await _world.DirectoryAsync(), "Монтажник");
        using var phone = host.CreateClient();
        using var other = host.CreateClient();
        var device = await JoinAsync(phone, me);
        var otherDevice = await JoinAsync(other, me);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        Assert.Empty(await db.DeviceObservations.ToListAsync());

        var observedAt = AssignmentHost.Now.AddMinutes(-5);
        using var first = await phone.PutAsJsonAsync(Observation, new
        {
            deviceId = device.DeviceId, apkVersionCode = 2, pendingReports = 2, oldestPendingDate = Date, observedAt,
        });
        first.EnsureSuccessStatusCode();
        var stored = await db.DeviceObservations.AsNoTracking().SingleAsync();
        Assert.Equal(2, stored.PendingReports);
        Assert.Equal(Day(Date), stored.OldestPendingDate);
        Assert.Equal(observedAt, stored.ObservedAt);
        Assert.Equal(AssignmentHost.Now, stored.ReceivedAt);

        try
        {
            host.ModuleTime = AssignmentHost.Now.AddMinutes(1);
            using var forged = await other.PutAsJsonAsync(Observation, new
            {
                deviceId = device.DeviceId, apkVersionCode = 3, pendingReports = 0, observedAt,
            });
            Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
            Assert.Equal(2, (await db.DeviceObservations.AsNoTracking().SingleAsync()).PendingReports);

            using var exchange = await phone.PutAsJsonAsync(Observation, new
            {
                apkVersionCode = 4, pendingReports = 0, observedAt = observedAt.AddMinutes(-1),
            });
            exchange.EnsureSuccessStatusCode();
            var empty = await db.DeviceObservations.AsNoTracking().SingleAsync();
            Assert.Equal(device.DeviceId, empty.DeviceId);
            Assert.NotEqual(otherDevice.DeviceId, empty.DeviceId);
            Assert.Equal(4, empty.ApkVersionCode);
            Assert.Equal(0, empty.PendingReports);
            Assert.Null(empty.OldestPendingDate);
            Assert.Equal(observedAt.AddMinutes(-1), empty.ObservedAt);
            Assert.Equal(host.ModuleTime, empty.ReceivedAt);
            Assert.Equal(host.ModuleTime, empty.LastContactAt);
            Assert.False(await db.Set<Report>().AnyAsync());
        }
        finally
        {
            host.ModuleTime = null;
        }
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("lost")]
    public async Task Revoked_devices_cannot_read_references_or_write_observations(string reason)
    {
        await _world.ResetAsync();
        var me = await _world.EmployeeAsync(await _world.DirectoryAsync(), "Монтажник");
        var (_, office) = await _world.UserAsync("Администратор устройств", WorkModule.DevicesManage, WorkModule.SitesAll);
        using var manager = office;
        using var phone = host.CreateClient();
        var device = await JoinAsync(phone, me);
        await FieldReportClient.RevokeAsync(manager, device, reason);
        await FieldReportClient.RefusedAsync(phone.GetAsync(References + "classifier"),
            HttpStatusCode.Forbidden, "device-revoked");
        await FieldReportClient.RefusedAsync(phone.PutAsJsonAsync(Observation, new
            { apkVersionCode = 1, pendingReports = 0, observedAt = AssignmentHost.Now }),
            HttpStatusCode.Forbidden, "device-revoked");
    }

    private async Task<DeviceCredentials> JoinAsync(HttpClient phone, Guid employee)
    {
        using var scope = host.Services.CreateScope();
        var (code, _) = await scope.ServiceProvider.GetRequiredService<DeviceSessions>().InviteAsync(employee, default);
        using var response = await phone.PostAsJsonAsync("/api/field/join", new { invitationCode = code });
        response.EnsureSuccessStatusCode();
        var credentials = (await response.Content.ReadFromJsonAsync<DeviceCredentials>())!;
        phone.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        return credentials;
    }

    private Task<Guid> AssignAsync(Guid employee, Guid site) =>
        _world.AssignedAsync(employee, site, AssignmentKind.Installer, "2026-10-30", "2026-11-02");

    private static async Task<JsonElement> PageAsync(HttpClient client, string query, string? cursor = null)
    {
        var separator = query.Contains('?') ? "&" : "?";
        var url = References + query + (cursor is null ? "" : $"{separator}cursor={Uri.EscapeDataString(cursor)}");
        using var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await JsonAsync(response);
    }

    private static Guid[] Ids(JsonElement page) =>
        [.. page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid())];

    private static string Cursor(JsonElement page) => page.GetProperty("cursor").GetString()!;

    private static string? Zone(JsonElement page) => page.GetProperty("metadata").GetProperty("companyTimeZoneId").GetString();
}
