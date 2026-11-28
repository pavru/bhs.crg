using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Integration.AssignmentWorld;
using static BHS.CRG.Tests.Integration.WorkMaterialsHintWorkOnlyTests;

namespace BHS.CRG.Tests.Integration;

public sealed class WorkMaterialsHintHost : AssignmentHost
{
    protected override string ModuleCode => "work,plan,costs";
    protected override string DatabaseSuffix => "whint";
}

public sealed class WorkMaterialsHintTests(WorkMaterialsHintHost host) : IClassFixture<WorkMaterialsHintHost>
{
    private readonly AssignmentWorld world = new(host);

    [Fact]
    public async Task Field_device_without_office_account_gets_narrow_hints_only_for_its_assignment()
    {
        await world.ResetAsync();
        var employee = await world.EmployeeAsync(await world.DirectoryAsync(), "Монтажник без аккаунта");
        var site = await world.SiteAsync("Назначенная");
        var emptySite = await world.SiteAsync("Без материалов");
        var otherSite = await world.SiteAsync("Чужая");
        var assignmentId = await world.AssignedAsync(employee, site, AssignmentKind.Installer, "2026-11-01", "2026-11-02");
        await world.AssignedAsync(employee, emptySite, AssignmentKind.Installer, "2026-11-01", "2026-11-02");
        var fixture = await SeedAsync(site, otherSite);
        var (_, manager) = await world.UserAsync("Администратор устройств", WorkModule.DevicesManage, WorkModule.SitesAll);
        using var office = manager;
        using var phone = host.CreateClient();
        await FieldReportClient.ConnectAsync(office, phone, employee);

        var issued = await PageAsync("issued-materials", site);
        Assert.Equal("ok", issued.GetProperty("state").GetString());
        var cable = Assert.Single(issued.GetProperty("items").EnumerateArray());
        Assert.Equal(fixture.Cable, cable.GetProperty("id").GetGuid());
        Assert.Equal(["displayName", "id", "unit"], cable.EnumerateObject().Select(p => p.Name).Order());

        var estimate = await PageAsync("estimated-materials", site, limit: 1);
        Assert.True(estimate.GetProperty("more").GetBoolean());
        foreach (var row in (await PageAsync("estimated-materials", site)).GetProperty("items").EnumerateArray())
            Assert.Equal(["displayName", "id", "planItemId"], row.EnumerateObject().Select(p => p.Name).Order());
        Assert.Contains((await PageAsync("estimated-materials", site)).GetProperty("items").EnumerateArray(),
            row => row.GetProperty("id").GetGuid() == fixture.Box && row.GetProperty("planItemId").GetGuid() == fixture.Position);

        foreach (var kind in new[] { "issued-materials", "estimated-materials" })
        {
            var empty = await PageAsync(kind, emptySite);
            Assert.Equal("ok", empty.GetProperty("state").GetString());
            Assert.Empty(empty.GetProperty("items").EnumerateArray());
            Assert.Equal("not-assigned", (await PageAsync(kind, otherSite)).GetProperty("state").GetString());
            Assert.Equal("not-assigned", (await PageAsync(kind, site, "2026-10-31")).GetProperty("state").GetString());
        }

        using var officeRequest = await phone.GetAsync(Path(site));
        Assert.Equal(HttpStatusCode.Unauthorized, officeRequest.StatusCode);
        const string catalogPath = "/api/field/v1/references/materials";
        var catalog = await phone.GetFromJsonAsync<JsonElement>(catalogPath);
        Assert.Contains(fixture.Cable, MaterialIds(catalog));
        var directory = new WorkTypeSeed(host);
        await directory.ArchiveAsync(fixture.Cable);
        await directory.ArchiveAsync(fixture.Box);
        var newIssued = await PageAsync("issued-materials", site, cursor: issued.GetProperty("cursor").GetString());
        Assert.True(newIssued.GetProperty("replace").GetBoolean());
        Assert.Empty(MaterialIds(newIssued));
        var newEstimate = await PageAsync("estimated-materials", site, cursor: estimate.GetProperty("cursor").GetString());
        Assert.True(newEstimate.GetProperty("replace").GetBoolean());
        Assert.DoesNotContain(fixture.Cable, MaterialIds(newEstimate));
        Assert.DoesNotContain(fixture.Box, MaterialIds(newEstimate));
        var newCatalog = await phone.GetFromJsonAsync<JsonElement>(catalogPath);
        Assert.DoesNotContain(fixture.Cable, MaterialIds(newCatalog));

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        await db.Assignments.Where(a => a.Id == assignmentId).ExecuteDeleteAsync();
        var lost = await PageAsync("estimated-materials", site, limit: 1, cursor: newEstimate.GetProperty("cursor").GetString());
        Assert.True(lost.GetProperty("replace").GetBoolean());
        Assert.Equal("not-assigned", lost.GetProperty("state").GetString());
        Assert.Empty(lost.GetProperty("items").EnumerateArray());

        static IEnumerable<Guid> MaterialIds(JsonElement page) =>
            page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid());

        async Task<JsonElement> PageAsync(string kind, Guid id, string date = "2026-11-01", int limit = 100, string? cursor = null)
        {
            var url = $"/api/field/v1/references/{kind}?siteId={id}&date={date}&limit={limit}";
            if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
            return await phone.GetFromJsonAsync<JsonElement>(url);
        }
    }

    [Fact]
    public async Task Три_яруса_по_полному_ключу_без_повторов_и_количеств()
    {
        await world.ResetAsync();
        var people = await world.DirectoryAsync();
        var employee = await world.EmployeeAsync(people, "Монтажник с подсказкой");
        var site = await world.SiteAsync("Жилой корпус А");
        var otherSite = await world.SiteAsync("Жилой корпус Б");
        var (reader, client) = await world.UserAsync("Монтажник", WorkModule.ReportOwn,
            CostsModule.MaterialsHint, PlanModule.EstimateHint);
        using var installer = client;
        await world.LinkAsync(employee, reader);
        await world.AssignedAsync(employee, site, AssignmentKind.Installer, "2026-10-01", "2026-11-30");

        var fixture = await SeedAsync(site, otherSite);
        var query = Path(site) + $"&workTypeId={fixture.Work}&unitId={fixture.Unit}&sectionId={fixture.Section}";
        foreach (var path in new[] { query, query + $"&planItemId={fixture.Position}", query + $"&planItemId={fixture.OtherPosition}" })
        {
            using var response = await installer.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await JsonAsync(response);
            var issued = Assert.Single(Items(result, "issued"));
            Assert.Equal(fixture.Cable, issued.GetProperty("id").GetGuid());
            Assert.Equal("м", issued.GetProperty("unit").GetString());
            Assert.Equal(["displayName", "id", "unit"], issued.EnumerateObject().Select(property => property.Name).Order());
            var estimate = Assert.Single(Items(result, "estimate"));
            Assert.Equal(fixture.Box, estimate.GetProperty("id").GetGuid());
            Assert.True(estimate.GetProperty("notIssued").GetBoolean());
            Assert.Equal(fixture.Catalog.Order(), Items(result, "catalog").Select(item => item.GetProperty("id").GetGuid()).Order());
        }

        using var missing = await installer.GetAsync(Path(site) + $"&workTypeId={fixture.Work}&unitId={fixture.Unit}");
        Assert.Equal("no-item", (await JsonAsync(missing)).GetProperty("estimate").GetProperty("state").GetString());

        var (_, withNoContracts) = await world.UserAsync("Менеджер без прав контрактов", WorkModule.ReportReview, WorkModule.SitesAll);
        using (withNoContracts)
        {
            foreach (var request in new[] { query, Path(site), Path(site) + $"&planItemId={fixture.Position}",
                         Path(site) + $"&planItemId={Guid.NewGuid()}" })
            {
                using var response = await withNoContracts.GetAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var result = await JsonAsync(response);
                AssertHidden(result, "issued", CostsModule.MaterialsHint);
                AssertHidden(result, "estimate", PlanModule.EstimateHint);
            }
        }

        var (_, planOnly) = await world.UserAsync("Менеджер с подсказкой сметы", WorkModule.ReportReview, WorkModule.SitesAll,
            PlanModule.EstimateHint);
        using (planOnly)
        using (var response = await planOnly.GetAsync(query))
        {
            var result = await JsonAsync(response);
            AssertHidden(result, "issued", CostsModule.MaterialsHint);
            Assert.Equal(2, Items(result, "estimate").Length);
            Assert.All(Items(result, "estimate"), item => Assert.False(item.TryGetProperty("notIssued", out var mark)
                && mark.ValueKind == JsonValueKind.True));
        }

        var directory = new WorkTypeSeed(host);
        using var searched = await installer.GetAsync(query + "&q=" + Uri.EscapeDataString("Гофра"));
        Assert.Equal(HttpStatusCode.OK, searched.StatusCode);
        var matches = await JsonAsync(searched);
        var matchedCable = Assert.Single(Items(matches, "issued"));
        Assert.Equal(fixture.Cable, matchedCable.GetProperty("id").GetGuid());
        Assert.Equal("Гофра кабельная", matchedCable.GetProperty("matchedAlias").GetString());
        Assert.Equal(fixture.Box, Assert.Single(Items(matches, "estimate")).GetProperty("id").GetGuid());
        Assert.Empty(Items(matches, "catalog"));

        await directory.ArchiveAsync(fixture.Cable);
        await directory.ArchiveAsync(fixture.Box);
        using var archived = await installer.GetAsync(query);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var afterArchive = await JsonAsync(archived);
        Assert.Empty(Items(afterArchive, "issued"));
        Assert.Empty(Items(afterArchive, "estimate"));
        Assert.Equal(2, afterArchive.GetProperty("catalog").GetProperty("inArchive").GetInt32());
    }

    private async Task<Fixture> SeedAsync(Guid site, Guid otherSite)
    {
        var seed = new WorkTypeSeed(host);
        var (unitType, workType) = await seed.ClassifierAsync();
        var metre = await seed.RecordAsync(unitType.Id, "м");
        var piece = await seed.RecordAsync(unitType.Id, "шт");
        var work = await seed.RecordAsync(workType.Id, "Прокладка кабеля", WorkTypeSeed.WorkTypeData("26.1", "Прокладка кабеля", metre));
        var materialType = await seed.TypeAsync(TestDirectories.NomenclatureCode, "Номенклатура");
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        await TestDirectories.AssignRolesAsync(services);
        var cable = await seed.SendAsync(new CreateCommonDataEntryCommand("Кабель", materialType.Id, JsonDocument.Parse("{}"),
            CatalogScope.System, null, Aliases: ["Гофра кабельная"]));
        var box = await seed.SendAsync(new CreateCommonDataEntryCommand("Коробка", materialType.Id, JsonDocument.Parse("{}"),
            CatalogScope.System, null, Aliases: ["Гофра коробочная"]));
        var clip = await seed.RecordAsync(materialType.Id, "Клипса");
        var tie = await seed.RecordAsync(materialType.Id, "Стяжка");
        var section = Section.Create(site, "ЭОМ");
        var core = services.GetRequiredService<AppDbContext>();
        core.Add(section);
        await core.SaveChangesAsync();

        var port = services.GetRequiredService<IModulePlanItems>();
        var wanted = new PlanItemKey(work.Id, site, section.Id, metre.Id);
        var otherUnit = wanted with { UnitId = piece.Id };
        var positions = await port.FindOrCreateManyAsync([wanted, otherUnit]);
        var position = positions.Items[wanted].Id;
        var otherPosition = positions.Items[otherUnit].Id;
        await services.ProjectModuleTypesAsync();
        var plan = services.GetRequiredService<PlanDbContext>();
        var estimate = Estimate.Create(site, null, "Тест сметных материалов", EstimateKind.Main, DateTimeOffset.UtcNow);
        plan.Estimates.Add(estimate);
        await plan.SaveChangesAsync();
        await services.GetRequiredService<EstimateRevisionWriter>().AddAsync(estimate.Id, new EstimateRevisionSource("fixture.gsfx"),
        [
            Row(position, metre.Id, cable.Id, box.Id), Row(otherPosition, piece.Id, clip.Id),
        ]);

        var costs = services.GetRequiredService<CostsDbContext>();
        Post(costs, site, cable.Id);
        Post(costs, otherSite, tie.Id);
        await costs.SaveChangesAsync();
        return new(work.Id, metre.Id, section.Id, position, otherPosition, cable.Id, box.Id, [clip.Id, tie.Id]);

        EstimateRowDraft Row(Guid item, Guid unitId, params Guid[] materials) => new("Работа", WorkTypeId: work.Id,
            UnitId: unitId, PlanItemId: item, Factor: 1m, Volume: 1m, SourceVolume: 1m,
            Materials: [.. materials.Select(id => new EstimateMaterialDraft("Ресурс", 2m, NomenclatureId: id))]);
    }

    private static void Post(CostsDbContext db, Guid site, Guid material)
    {
        var day = new DateOnly(2026, 10, 10);
        var bill = Waybill.Create(null);
        bill.Apply(new WaybillHeader("26", day, null, site, null, null));
        bill.Post(null, day);
        var line = WaybillLine.Create(bill.Id);
        line.Apply(1, new WaybillLineValues(material, "Из накладной", "м", 10m, null));
        db.AddRange(bill, line);
    }

    private static JsonElement[] Items(JsonElement response, string tier) =>
        response.GetProperty(tier).GetProperty("items").EnumerateArray().ToArray();

    private static void AssertHidden(JsonElement response, string key, string permission)
    {
        var tier = response.GetProperty(key);
        Assert.Equal("hidden", tier.GetProperty("state").GetString());
        Assert.Equal(permission, tier.GetProperty("permission").GetString());
        Assert.Empty(Items(response, key));
    }

    private sealed record Fixture(Guid Work, Guid Unit, Guid Section, Guid Position, Guid OtherPosition, Guid Cable,
        Guid Box, Guid[] Catalog);
}
