using System.Data.Common;
using System.Net;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Tests.Integration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
        AddWaybill(db, Guid.NewGuid(), date, posted: true, (Guid.NewGuid(), "шт", 1m));
        await db.SaveChangesAsync();

        ContractAnswer<MaterialsOnSite> answer;
        host.ReadProbe.Context = db;
        try
        {
            answer = await Connected(scope.ServiceProvider).ReadAsync(Reader(CostsModule.MaterialsHint), site);
        }
        finally
        {
            host.ReadProbe.Context = null;
        }

        var read = Assert.Single(host.ReadProbe.Reads);
        Assert.StartsWith("SELECT DISTINCT", read.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.False(read.InTransaction);
        var hint = Shown<MaterialsOnSite.Hint>(answer);
        Assert.Equal(cable, Assert.Single(hint.NomenclatureIds));
        var json = JsonSerializer.SerializeToElement(hint, JsonSerializerOptions.Web);
        Assert.Equal("nomenclatureIds", Assert.Single(json.EnumerateObject()).Name);
    }

    [Fact]
    public async Task Явная_подсказка_читает_одну_узкую_проекцию_даже_при_широком_праве()
    {
        using var scope = host.Services.CreateScope();
        var site = Guid.NewGuid();
        var cable = Guid.NewGuid();
        var box = Guid.NewGuid();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        AddWaybill(db, site, new DateOnly(2026, 9, 10), posted: true,
            (cable, "м", 10m), (cable, "бухта", 1m), (box, "шт", 2m), (box, "шт", 5m));
        AddWaybill(db, Guid.NewGuid(), new DateOnly(2026, 9, 10), posted: true, (Guid.NewGuid(), "м", 10m));
        await db.SaveChangesAsync();

        var contract = Connected(scope.ServiceProvider);
        foreach (var reader in new[] { Reader(CostsModule.MaterialsHint), Reader(CostsModule.MaterialsRead),
                     Reader(CostsModule.MaterialsRead, CostsModule.MaterialsHint) })
        {
            host.ReadProbe.Context = db;
            ContractAnswer<IReadOnlyList<MaterialOnSiteHint>> answer;
            try { answer = await contract.ReadHintAsync(reader, site); }
            finally { host.ReadProbe.Context = null; }

            var read = Assert.Single(host.ReadProbe.Reads);
            var columns = read.Statement.Split("FROM", 2, StringSplitOptions.None)[0];
            Assert.StartsWith("SELECT DISTINCT", columns, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("quantity", columns, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("issued_on", columns, StringComparison.OrdinalIgnoreCase);
            Assert.False(read.InTransaction);
            var shown = Assert.IsType<ContractAnswer<IReadOnlyList<MaterialOnSiteHint>>.Shown>(answer).Value;
            Assert.Equal(2, shown.Count);
            Assert.Contains(new MaterialOnSiteHint(cable, null), shown);
            Assert.Contains(new MaterialOnSiteHint(box, "шт"), shown);
            var json = JsonSerializer.SerializeToElement(shown[0], JsonSerializerOptions.Web);
            Assert.Equal(["nomenclatureId", "unit"], json.EnumerateObject().Select(field => field.Name).Order());
        }
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
                // У менеджера модуль открыт правами заявок (Plan-26). Контракт по-прежнему не открывает
                // ни счета, ни накладные.
                Assert.Equal(role.Name == "ProjectManager",
                    Assert.Single(access.Modules, module => module.Code == CostsModule.ModuleCode).Available);
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
    internal MaterialsReadProbe ReadProbe { get; } = new();

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
        builder.ConfigureTestServices(services =>
            services.AddDbContext<CostsDbContext>(options => options.AddInterceptors(ReadProbe)));
    }
}

internal sealed class MaterialsReadProbe : DbCommandInterceptor
{
    private CostsDbContext? context;

    public CostsDbContext? Context
    {
        private get => context;
        set
        {
            if (value is not null) Reads.Clear();
            context = value;
        }
    }

    public List<(string Statement, bool InTransaction)> Reads { get; } = [];

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (Context is not null && eventData.Context == Context)
            Reads.Add((command.CommandText, command.Transaction is not null));
        return ValueTask.FromResult(result);
    }
}
