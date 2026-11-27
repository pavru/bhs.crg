using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Api.Modules;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Plan;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NPOI.XSSF.UserModel;

namespace BHS.CRG.Tests.Costs;

[Collection("Integration")]
public class EstimateVsCostsTests(EstimateVsCostsHost host) : IClassFixture<EstimateVsCostsHost>
{
    private const string Path = "/api/costs/estimate-vs-costs";

    [Fact]
    public async Task HTTP_разноска_черновика_без_цены_не_становится_нулевой_полной_стоимостью()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var material = (await EstimateSeed.ClassifierAsync(host)).Nomenclature;
        await PlanAsync(site, material, 100, ResourceTaxBasis.ExcludingVat);
        await InvoiceTypeAsync();
        using var client = await GrantedSignIn.WithPermissionsAsync(host, CostsModule.ReportRead, PlanModule.EstimateCost,
            "costs.invoice.read", "costs.invoice.edit", "costs.allocation.edit");
        var created = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites = new { } });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var saved = await client.PutAsJsonAsync($"/api/costs/invoices/{id}/lines", new { lines = new[] { new {
            nomenclature = new Dictionary<string, object?> { ["$ref"] = "catalog", ["entryId"] = material.ToString() },
            quantity = 2m,
        } } });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var line = Assert.Single((await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lines").EnumerateArray());
        var allocated = await client.PutAsJsonAsync($"/api/costs/invoices/{id}/lines/{line.GetProperty("id").GetGuid()}/allocation",
            new { parts = new[] { new { construction = site.Construction.ToString(), section = site.Section.ToString(), quantity = 2m } } });
        Assert.True(allocated.IsSuccessStatusCode, await allocated.Content.ReadAsStringAsync());
        var row = Assert.Single((await GetAsync(client, site.Construction)).GetProperty("rows").EnumerateArray());
        Assert.Equal(0m, row.GetProperty("allocated").GetDecimal());
        Assert.True(row.GetProperty("allocatedIncomplete").GetBoolean());
        Assert.False(row.TryGetProperty("difference", out _));
        using var xlsx = await XlsxAsync(client, site.Construction);
        Assert.Contains(Cells(xlsx), text => text.Contains("известная часть") && text.Contains("Стоимость остальных долей неизвестна"));
    }

    [Fact]
    public async Task Объединение_номенклатуры_разных_позиций_сохраняет_неполноту_и_неизвестную_основу()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var other = site with { Section = await DistributionFlow.SectionAsync(host, site.Construction, "Вторая позиция") };
        var material = (await EstimateSeed.ClassifierAsync(host)).Nomenclature;
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var first = (await EstimateSeed.MatchedRowAsync(host, site, 100)) with {
            Materials = [new EstimateMaterialDraft("Кабель", 1, NomenclatureId: material, Amount: 100,
                AmountBasis: ResourceTaxBasis.ExcludingVat)],
        };
        var second = (await EstimateSeed.MatchedRowAsync(host, other, 100)) with {
            Materials = [new EstimateMaterialDraft("Кабель", 1, NomenclatureId: material, Amount: 20,
                AmountBasis: ResourceTaxBasis.Unknown)],
        };
        await EstimateSeed.RevisionAsync(host, estimate, first, second);
        using var client = await ReaderAsync();
        var row = Assert.Single((await GetAsync(client, site.Construction)).GetProperty("rows").EnumerateArray());
        Assert.Equal(120, row.GetProperty("estimate").GetProperty("amount").GetDecimal());
        Assert.True(row.GetProperty("estimate").GetProperty("basisUnknown").GetBoolean());
        Assert.False(row.TryGetProperty("difference", out _));
        var thirdEstimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        await EstimateSeed.RevisionAsync(host, thirdEstimate, first with {
            Materials = [new EstimateMaterialDraft("Кабель", 1, NomenclatureId: material)],
        });
        row = Assert.Single((await GetAsync(client, site.Construction)).GetProperty("rows").EnumerateArray());
        Assert.False(row.GetProperty("estimate").TryGetProperty("amount", out _));
        Assert.False(row.TryGetProperty("difference", out _));
    }

    [Fact]
    public async Task Настоящий_импорт_через_контракт_даёт_стоимость_и_отклонение()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var material = (await EstimateSeed.ClassifierAsync(host)).Nomenclature;
        using var client = await GrantedSignIn.WithPermissionsAsync(host,
            CostsModule.ReportRead, PlanModule.EstimateRead, PlanModule.EstimateEdit, PlanModule.EstimateCost);
        var (estimate, revision, _) = await ResourceCostFlow.ImportAsync(client, site.Construction, "lsr-16.2.2-sks");
        Guid resource;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlanDbContext>();
            resource = await (from m in db.EstimateRowMaterials
                              join r in db.EstimateRows on m.RowId equals r.Id
                              where r.RevisionId == revision && r.Number == "85"
                              select m.Id).SingleAsync();
        }
        await ResourceCostFlow.MatchAsync(client, estimate, revision, resource, material);
        await InvoiceAsync(site, material, 20_388.38m, 0, paid: false);

        var report = await GetAsync(client, site.Construction);
        var row = Assert.Single(report.GetProperty("rows").EnumerateArray());
        Assert.Equal(388.38m, row.GetProperty("estimate").GetProperty("amount").GetDecimal());
        Assert.Equal(20_000m, row.GetProperty("difference").GetDecimal());
        Assert.Equal(0, row.GetProperty("paid").GetDecimal());
        Assert.False(row.GetProperty("estimate").GetProperty("basisUnknown").GetBoolean());
        // Новая редакция, пока прежняя действующая, не удваивает стоимость.
        await ResourceCostFlow.ImportAsync(client, site.Construction, "lsr-16.2.2-sks", estimate);
        Assert.Equal(388.38m, Assert.Single((await GetAsync(client, site.Construction)).GetProperty("rows")
            .EnumerateArray()).GetProperty("estimate").GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Факт_сходится_с_CostViews_и_учитывает_черновики_календарь_и_разные_доли()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var material = (await EstimateSeed.ClassifierAsync(host)).Nomenclature;
        await InvoiceAsync(site, material, 48_000, 8_000, paid: true); // оплаченный черновик, 40 000 без НДС
        await InvoiceAsync(site, material, 72_000, 12_000, paid: false);
        await InvoiceAsync(site, material, 25_000, 0, paid: false, rejected: true);
        await InvoiceAsync(site, material, 999, 0, paid: true, on: new DateOnly(2019, 12, 1));
        await InvoiceAsync(site, null, 300, 0, paid: false);
        await InvoiceAsync(site, null, 700, 0, paid: false, withoutLine: true);
        var otherSection = await DistributionFlow.SectionAsync(host, site.Construction, "Другой раздел");
        await InvoiceAsync(site with { Section = otherSection }, material, 200, 0, paid: true);
        using var client = await ReaderAsync();

        var report = await GetAsync(client, site.Construction);
        DateOnly today;
        using (var scope = host.Services.CreateScope())
            today = await scope.ServiceProvider.GetRequiredService<IModuleClock>().TodayAsync();
        var totals = await client.GetFromJsonAsync<JsonElement>(
            $"/api/costs/site-costs?site={site.Construction}&from=2020-01&to={today:yyyy-MM}&vat=without");
        Assert.Equal(totals.GetProperty("total").GetProperty("amount").GetDecimal(), report.GetProperty("paid").GetDecimal());
        Assert.Equal(101_200m, report.GetProperty("allocated").GetDecimal());
        Assert.Equal(40_200m, report.GetProperty("paid").GetDecimal());
        Assert.Equal(report.GetProperty("paid").GetDecimal() + totals.GetProperty("payable").GetProperty("amount").GetDecimal(),
            report.GetProperty("allocated").GetDecimal());
        var none = report.GetProperty("rows").EnumerateArray().Single(row => row.GetProperty("nomenclatureId").ValueKind == JsonValueKind.Null);
        Assert.Equal(1000, none.GetProperty("allocated").GetDecimal());
        var section = await GetAsync(client, site.Construction, site.Section);
        Assert.Equal(101_000m, section.GetProperty("allocated").GetDecimal());
        Assert.Equal(40_000m, section.GetProperty("paid").GetDecimal());
    }

    [Fact]
    public async Task Отсутствующие_пары_неполные_суммы_основы_и_неизвестный_НДС_сохраняют_причины()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var material = (await EstimateSeed.ClassifierAsync(host)).Nomenclature;
        await PlanAsync(site, material, 100m, ResourceTaxBasis.ExcludingVat);
        await InvoiceAsync(site, material, 120m, 20m, paid: true);
        using var client = await ReaderAsync();
        var row = Assert.Single((await GetAsync(client, site.Construction)).GetProperty("rows").EnumerateArray());
        Assert.Equal(0m, row.GetProperty("difference").GetDecimal());
        await InvoiceAsync(site, material, 120m, null, paid: false);
        row = Assert.Single((await GetAsync(client, site.Construction)).GetProperty("rows").EnumerateArray());
        Assert.Equal(220m, row.GetProperty("allocated").GetDecimal());
        Assert.True(row.GetProperty("allocatedVatUnknown").GetBoolean());
        Assert.False(row.TryGetProperty("difference", out _));

        var unknown = await EstimateSeed.SiteAsync(host);
        await PlanAsync(unknown, material, 100m, ResourceTaxBasis.Unknown);
        row = Assert.Single((await GetAsync(client, unknown.Construction)).GetProperty("rows").EnumerateArray());
        Assert.True(row.GetProperty("estimate").GetProperty("basisUnknown").GetBoolean());
        Assert.Equal(100m, row.GetProperty("estimate").GetProperty("amount").GetDecimal());
        Assert.False(row.TryGetProperty("difference", out _));
        using (var xlsx = await XlsxAsync(client, unknown.Construction))
        {
            Assert.Contains(Cells(xlsx), text => text.Contains("Основа сметной суммы без НДС не подтверждена"));
            Assert.Contains(xlsx.GetSheetAt(0).GetRow(xlsx.GetSheetAt(0).LastRowNum).Cells,
                cell => cell.CellType == NPOI.SS.UserModel.CellType.Blank);
        }
        await PlanAsync(unknown, material, null, ResourceTaxBasis.Unknown);
        row = Assert.Single((await GetAsync(client, unknown.Construction)).GetProperty("rows").EnumerateArray());
        Assert.Equal("not-in-estimate", row.GetProperty("estimate").GetProperty("state").GetString());
        Assert.False(row.GetProperty("estimate").TryGetProperty("amount", out _));

        var onlyPlan = await EstimateSeed.SiteAsync(host);
        await PlanAsync(onlyPlan, material, 100, ResourceTaxBasis.ExcludingVat);
        row = Assert.Single((await GetAsync(client, onlyPlan.Construction)).GetProperty("rows").EnumerateArray());
        Assert.Equal((0m, 0m, -100m), (row.GetProperty("allocated").GetDecimal(),
            row.GetProperty("paid").GetDecimal(), row.GetProperty("difference").GetDecimal()));
    }

    [Fact]
    public async Task Права_скрывают_смету_и_XLSX_выгружает_только_разрешённые_колонки()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var material = (await EstimateSeed.ClassifierAsync(host)).Nomenclature;
        await PlanAsync(site, material, 12345.67m, ResourceTaxBasis.ExcludingVat);
        await InvoiceAsync(site, material, 120m, null, paid: false);
        using var accountant = await GrantedSignIn.WithPermissionsAsync(host, CostsModule.ReportRead);
        var choices = await accountant.GetFromJsonAsync<JsonElement>(Path + "/constructions");
        Assert.Contains(choices.EnumerateArray(), choice => choice.GetProperty("id").GetGuid() == site.Construction);
        var report = await GetAsync(accountant, site.Construction);
        Assert.Equal("hidden", report.GetProperty("estimateState").GetString());
        Assert.Equal("plan.estimate.cost", report.GetProperty("permission").GetString());
        var row = Assert.Single(report.GetProperty("rows").EnumerateArray());
        Assert.False(row.GetProperty("estimate").TryGetProperty("amount", out _));
        Assert.False(row.TryGetProperty("difference", out _));
        using var denied = await GrantedSignIn.WithPermissionsAsync(host, "costs.invoice.read");
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync($"{Path}?site={site.Construction}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync($"{Path}/export?site={site.Construction}")).StatusCode);

        using var book = await XlsxAsync(accountant, site.Construction);
        var cells = Cells(book);
        Assert.DoesNotContain(cells, text => text.StartsWith("Смета без НДС"));
        Assert.DoesNotContain(cells, text => text.StartsWith("Отклонение"));
        Assert.DoesNotContain("12345.67", cells);
        Assert.Contains(cells, text => text.Contains("НДС неизвестен"));
        using var manager = await ReaderAsync();
        using var shown = await XlsxAsync(manager, site.Construction);
        Assert.Contains("Смета без НДС, RUB", Cells(shown));
        Assert.Contains("Отклонение, RUB", Cells(shown));
        Assert.Contains(shown.GetSheetAt(0).GetRow(shown.GetSheetAt(0).LastRowNum).Cells,
            cell => cell.CellType == NPOI.SS.UserModel.CellType.Blank);
    }

    private Task<HttpClient> ReaderAsync() =>
        GrantedSignIn.WithPermissionsAsync(host, CostsModule.ReportRead, PlanModule.EstimateCost);

    internal static Task<JsonElement> GetAsync(HttpClient client, Guid site, Guid? section = null) =>
        client.GetFromJsonAsync<JsonElement>($"{Path}?site={site}" + (section is null ? "" : $"&section={section}"));

    internal static async Task<XSSFWorkbook> XlsxAsync(HttpClient client, Guid site)
    {
        var response = await client.GetAsync($"{Path}/export?site={site}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return new XSSFWorkbook(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
    }

    internal static string[] Cells(XSSFWorkbook book) =>
        book.GetSheetAt(0).SelectMany(row => row.Cells).Select(cell => cell.ToString() ?? "").ToArray();

    internal async Task PlanAsync(EstimateSeed.Site site, Guid material, decimal? amount, ResourceTaxBasis basis)
    {
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        await EstimateSeed.RevisionAsync(host, estimate, new EstimateRowDraft("Материал",
            Kind: EstimateRowKind.Material, SourceVolume: 1,
            Materials: [new EstimateMaterialDraft("Материал", 1, NomenclatureId: material, Amount: amount, AmountBasis: basis)]));
    }

    internal async Task InvoiceAsync(EstimateSeed.Site site, Guid? material, decimal amount, decimal? vat,
        bool paid, bool rejected = false, bool withoutLine = false, DateOnly? on = null)
    {
        var typeId = await InvoiceTypeAsync();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var invoice = Invoice.Create(typeId, null, null);
        invoice.ApplyRequisites(new("T-" + Guid.NewGuid(), new DateOnly(2026, 11, 20), null, null, null,
            amount, vat, null, null, null), JsonDocument.Parse("{}"), false, new(false));
        if (rejected) invoice.Reject();
        var date = on ?? await scope.ServiceProvider.GetRequiredService<IModuleClock>().TodayAsync();
        if (paid) invoice.Pay(date, null, null);
        InvoiceLine? line = null;
        if (!withoutLine)
        {
            line = InvoiceLine.Create(invoice.Id);
            line.Apply(1, new InvoiceLineValues(material, "Материал", null, null, material is null ? null : 1,
                null, null, vat, amount, null).Completed(VatMode.Included));
            db.InvoiceLines.Add(line);
        }
        var part = InvoiceAllocation.Create(invoice.Id, line?.Id);
        part.Apply(1, new(AllocationTarget.Site(site.Construction, site.Section),
            line?.Quantity, line?.Quantity is null ? amount : null));
        if (paid) part.Post(date);
        db.Invoices.Add(invoice);
        db.InvoiceAllocations.Add(part);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> InvoiceTypeAsync()
    {
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        if ((await types.FindAsync(type => type.Code == TestDirectories.OrganizationCode)).Count == 0)
            await new WorkTypeSeed(host).TypeAsync(TestDirectories.OrganizationCode, "Организация");
        await TestDirectories.AssignRolesAsync(scope.ServiceProvider);
        await scope.ServiceProvider.ProjectModuleTypesAsync();
        return Assert.Single(await types.FindAsync(type => type.Code == CostsRecordTypes.InvoiceCode)).Id;
    }
}

public class EstimateVsCostsHost : ModuleOnlyHost
{
    protected override string ModuleCode => "costs,plan";
    protected override string DatabaseSuffix => "plan27";
}

[Collection("Integration")]
public class EstimateVsCostsDisconnectedTests(EstimateVsCostsDisconnectedHost host) : IClassFixture<EstimateVsCostsDisconnectedHost>
{
    [Fact]
    public async Task Выключенный_Plan_показан_как_неподключённый_и_пустой_факт_доступен()
    {
        var site = await EstimateSeed.SiteAsync(host);
        using var reader = await GrantedSignIn.WithPermissionsAsync(host, CostsModule.ReportRead);
        var report = await reader.GetFromJsonAsync<JsonElement>($"/api/costs/estimate-vs-costs?site={site.Construction}");
        Assert.Equal("not-connected", report.GetProperty("estimateState").GetString());
        Assert.Contains("Планирование", report.GetProperty("message").GetString());
        Assert.Empty(report.GetProperty("rows").EnumerateArray());
        Assert.Equal(0, report.GetProperty("allocated").GetDecimal());
    }
}

public sealed class EstimateVsCostsDisconnectedHost : ModuleOnlyHost
{
    protected override string ModuleCode => "costs";
    protected override string DatabaseSuffix => "plan27off";
}

[Collection("Integration")]
public class EstimateVsCostsCurrencyTests(EstimateVsCostsCurrencyHost host) : IClassFixture<EstimateVsCostsCurrencyHost>
{
    [Fact]
    public async Task Рубли_сметы_и_USD_факта_сохраняются_со_своими_валютами_без_ложного_нулевого_отклонения()
    {
        var site = await EstimateSeed.SiteAsync(host);
        using var reader = await GrantedSignIn.WithPermissionsAsync(host,
            "core.system.manage", CostsModule.ReportRead, PlanModule.EstimateCost);
        Assert.Equal(HttpStatusCode.NoContent,
            (await reader.PutAsJsonAsync("/api/settings/company/currency", new { currency = "USD" })).StatusCode);
        var flow = new EstimateVsCostsTests(host);
        var material = (await EstimateSeed.ClassifierAsync(host)).Nomenclature;
        await flow.PlanAsync(site, material, 100m, ResourceTaxBasis.ExcludingVat);
        await flow.InvoiceAsync(site, material, 100m, 0m, paid: false);
        var report = await EstimateVsCostsTests.GetAsync(reader, site.Construction);
        Assert.Equal("RUB", report.GetProperty("estimateCurrency").GetString());
        Assert.Equal("USD", report.GetProperty("factCurrency").GetString());
        Assert.True(report.GetProperty("currencyMismatch").GetBoolean());
        var row = Assert.Single(report.GetProperty("rows").EnumerateArray());
        Assert.Equal(100m, row.GetProperty("estimate").GetProperty("amount").GetDecimal());
        Assert.Equal(100m, row.GetProperty("allocated").GetDecimal());
        Assert.False(row.TryGetProperty("difference", out _));
        using var book = await EstimateVsCostsTests.XlsxAsync(reader, site.Construction);
        Assert.Contains("Смета без НДС, RUB", EstimateVsCostsTests.Cells(book));
        Assert.Contains("Разнесено без НДС, USD", EstimateVsCostsTests.Cells(book));
        var data = book.GetSheetAt(0).GetRow(book.GetSheetAt(0).LastRowNum);
        Assert.Equal(100d, data.GetCell(1).NumericCellValue);
        Assert.Equal(100d, data.GetCell(2).NumericCellValue);
        Assert.Equal(NPOI.SS.UserModel.CellType.Blank, data.GetCell(4).CellType);
    }
}

public sealed class EstimateVsCostsCurrencyHost : EstimateVsCostsHost
{
    protected override string DatabaseSuffix => "plan27fx";
}
