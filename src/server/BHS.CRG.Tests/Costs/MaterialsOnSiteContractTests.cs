using System.Net;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Costs;

[Collection("Integration")]
public sealed class MaterialsOnSiteContractTests(MaterialsOnSiteHost host) : IClassFixture<MaterialsOnSiteHost>
{
    [Fact]
    public async Task Полный_ответ_сводит_отпуск_одной_стройки_и_считает_несопоставленные_строки()
    {
        using var scope = host.Services.CreateScope();
        var site = Guid.NewGuid();
        var cable = Guid.NewGuid();
        var first = new DateOnly(2026, 9, 10);
        var last = first.AddDays(2);
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        AddWaybill(db, site, first, posted: true, (cable, "м", 10m), (null, "шт", 1m));
        AddWaybill(db, site, last, posted: true, (cable, "м", 2m));
        AddWaybill(db, site, last.AddDays(1), posted: false, (cable, "м", 100m), (null, "шт", 1m));
        AddWaybill(db, Guid.NewGuid(), last.AddDays(1), posted: true, (cable, "м", 200m), (null, "шт", 1m));
        await db.SaveChangesAsync();

        var contract = Connected(scope.ServiceProvider);
        var answer = await contract.ReadAsync(Reader(CostsModule.MaterialsRead), site);
        var full = Shown<MaterialsOnSite.Full>(answer);
        var item = Assert.Single(full.Items);
        Assert.Equal(new MaterialOnSite(cable, "м", 12m, first, last), item);
        Assert.Equal(1, full.UnmatchedLines);
        var json = JsonSerializer.SerializeToElement(item, JsonSerializerOptions.Web);
        Assert.Equal(["first", "last", "nomenclatureId", "quantity", "unit"],
            json.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));

        var withBoth = await contract.ReadAsync(Reader(CostsModule.MaterialsRead, CostsModule.MaterialsHint), site);
        Assert.Equal(item, Assert.Single(Shown<MaterialsOnSite.Full>(withBoth).Items));
    }

    [Fact]
    public async Task Стройка_только_с_черновиком_даёт_пустой_перечень_при_обоих_правах()
    {
        using var scope = host.Services.CreateScope();
        var site = Guid.NewGuid();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        AddWaybill(db, site, new DateOnly(2026, 9, 10), posted: false, (Guid.NewGuid(), "м", 10m), (null, "шт", 1m));
        await db.SaveChangesAsync();

        var contract = Connected(scope.ServiceProvider);
        var full = Shown<MaterialsOnSite.Full>(await contract.ReadAsync(Reader(CostsModule.MaterialsRead), site));
        var hint = Shown<MaterialsOnSite.Hint>(await contract.ReadAsync(Reader(CostsModule.MaterialsHint), site));
        Assert.Empty(full.Items);
        Assert.Equal(0, full.UnmatchedLines);
        Assert.Empty(hint.NomenclatureIds);
    }

    [Fact]
    public async Task Подсказка_содержит_только_уникальные_позиции_из_проведённых_накладных()
    {
        using var scope = host.Services.CreateScope();
        var site = Guid.NewGuid();
        var cable = Guid.NewGuid();
        var date = new DateOnly(2026, 9, 10);
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        AddWaybill(db, site, date, posted: true, (cable, "м", 10m), (cable, "бухта", 1m), (null, "шт", 1m));
        AddWaybill(db, site, date, posted: false, (Guid.NewGuid(), "шт", 2m));
        await db.SaveChangesAsync();

        var answer = await Connected(scope.ServiceProvider).ReadAsync(Reader(CostsModule.MaterialsHint), site);
        var hint = Shown<MaterialsOnSite.Hint>(answer);
        Assert.Equal(cable, Assert.Single(hint.NomenclatureIds));
        var json = JsonSerializer.SerializeToElement(hint, JsonSerializerOptions.Web);
        Assert.Equal("nomenclatureIds", Assert.Single(json.EnumerateObject()).Name);
    }

    [Fact]
    public async Task Без_права_данные_скрыты_а_фоновому_заданию_отказано_с_причиной()
    {
        using var scope = host.Services.CreateScope();
        var site = Guid.NewGuid();
        var contract = Connected(scope.ServiceProvider);
        var hidden = Assert.IsType<ContractAnswer<MaterialsOnSite>.Hidden>(
            await contract.ReadAsync(Reader("costs.waybill.read"), site));
        Assert.Equal(CostsModule.MaterialsRead, hidden.Permission);

        const string reason = "фоновая проверка";
        var refused = Assert.IsType<ContractAnswer<MaterialsOnSite>.SystemRefused>(
            await contract.ReadAsync(ModuleReader.OfSystem(reason), site));
        Assert.Equal(reason, refused.Reason);
    }

    [Fact]
    public async Task Системные_роли_получают_свои_права_контракта()
    {
        foreach (var role in SystemRoles.All)
        {
            using var client = await AccessProbe.SignInAsync(host, role.Name, "materials");
            var access = await AccessProbe.AccessAsync(client);
            var read = role.Name is SystemRoles.Admin or SystemRoles.IdEngineer or "ProjectManager";
            var hint = role.Name is SystemRoles.Admin or "Installer";
            Assert.Equal(read, access.Permissions.Contains(CostsModule.MaterialsRead));
            Assert.Equal(hint, access.Permissions.Contains(CostsModule.MaterialsHint));

            if (role.Name is SystemRoles.IdEngineer or "Installer" or "ProjectManager")
            {
                Assert.False(Assert.Single(access.Modules, module => module.Code == CostsModule.ModuleCode).Available);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/costs/invoices")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/costs/waybills")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden,
                    (await client.GetAsync($"/api/costs/materials?constructionId={Guid.NewGuid()}")).StatusCode);
            }
        }
    }

    private static IMaterialsOnSite Connected(IServiceProvider services) =>
        Assert.IsType<ModuleContractAnswer<IMaterialsOnSite>.Connected>(
            services.GetRequiredService<IModuleContracts>().Get<IMaterialsOnSite>()).Contract;

    private static ModuleReader Reader(params string[] permissions) => ModuleReader.Of(Guid.NewGuid(), "Испытатель", permissions);

    private static T Shown<T>(ContractAnswer<MaterialsOnSite> answer) where T : MaterialsOnSite =>
        Assert.IsType<T>(Assert.IsType<ContractAnswer<MaterialsOnSite>.Shown>(answer).Value);

    private static void AddWaybill(CostsDbContext db, Guid site, DateOnly day, bool posted,
        params (Guid? NomenclatureId, string Unit, decimal Quantity)[] materials)
    {
        var waybill = Waybill.Create(null);
        waybill.Apply(new WaybillHeader(null, day, null, site, null, null));
        if (posted) waybill.Post(null, day);
        db.Waybills.Add(waybill);

        for (var index = 0; index < materials.Length; index++)
        {
            var material = materials[index];
            var line = WaybillLine.Create(waybill.Id);
            line.Apply(index + 1, new WaybillLineValues(material.NomenclatureId, "Из накладной", material.Unit,
                material.Quantity, null));
            db.WaybillLines.Add(line);
        }
    }
}

public sealed class MaterialsOnSiteHost : IntegrationTestFixture
{
    protected override string HostConnectionString => TestDatabases.ConnectionString("materials");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var overrides = new Dictionary<string, string?>
        {
            ["Modules:Enabled"] = "costs,work",
            ["ConnectionStrings:Postgres"] = HostConnectionString,
        };
        foreach (var (key, value) in overrides) builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(overrides));
    }
}
