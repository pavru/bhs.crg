using System.Net;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
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
        using (var response = await withNoContracts.GetAsync(query))
        {
            var result = await JsonAsync(response);
            AssertHidden(result, "issued", CostsModule.MaterialsHint);
            AssertHidden(result, "estimate", PlanModule.EstimateHint);
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
        var cable = await seed.RecordAsync(materialType.Id, "Кабель");
        var box = await seed.RecordAsync(materialType.Id, "Коробка");
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
