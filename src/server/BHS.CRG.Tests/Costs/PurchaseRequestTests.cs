using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Plan;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Costs;

public sealed partial class PurchaseRequestTests(PurchaseRequestHost host) : IClassFixture<PurchaseRequestHost>
{
    internal const string Requests = "/api/costs/requests";
    private const string Suggest = Requests + "/draft-from-estimate";

    [Theory]
    [InlineData(30, "м", 70, 30, false, false)]
    [InlineData(120, "м", 0, 120, true, false)]
    [InlineData(1, "бухта", 100, null, false, true)]
    [InlineData(10, null, 100, null, false, true)]
    [InlineData(0, "м", 100, 0, false, false)]
    public async Task Предложение_различает_отпуск_и_несовместимые_единицы(
        int issued, string? unit, int wanted, int? compatible, bool exceeds, bool mismatch)
    {
        var (site, material, item, _) = await SeedAsync(100);
        if (issued > 0) await IssueAsync(site.Construction, material, issued, unit);
        await IssueAsync(site.Construction, null, 1, "м");
        await IssueAsync(site.Construction, material, 100, "м", posted: false);
        using var client = await SupplierAsync();

        var suggestion = await SuggestAsync(client, new { constructionId = site.Construction, planItemIds = new[] { item } });

        var line = Assert.Single(suggestion.GetProperty("lines").EnumerateArray());
        Assert.Equal(100m, line.GetProperty("estimateQuantity").GetDecimal());
        Assert.Equal(wanted, line.GetProperty("quantity").GetDecimal());
        Assert.Equal((decimal?)compatible, line.GetProperty("compatibleIssuedQuantity").Deserialize<decimal?>());
        Assert.Equal(exceeds, line.GetProperty("issuedExceeds").GetBoolean());
        Assert.Equal(mismatch, line.GetProperty("unitMismatch").GetBoolean());
        Assert.Equal(item, line.GetProperty("planItemId").GetGuid());
        Assert.Equal(1, suggestion.GetProperty("unmatchedIssued").GetInt32());
        var list = await client.GetFromJsonAsync<JsonElement>($"{Requests}?constructionId={site.Construction}");
        Assert.Empty(list.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Отпуск_вычитается_один_раз_и_заявка_не_резервирует_объём()
    {
        var (site, material, a, b) = await SeedAsync(60, 40);
        await IssueAsync(site.Construction, material, 30, "м");
        using var client = await SupplierAsync();
        var all = new { constructionId = site.Construction, planItemIds = new[] { a, b!.Value } };

        var suggestion = await SuggestAsync(client, all);
        Assert.Equal("planItems", suggestion.GetProperty("selection").GetString());
        var line = Assert.Single(suggestion.GetProperty("lines").EnumerateArray());
        Assert.Equal(70m, line.GetProperty("quantity").GetDecimal());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("planItemId").ValueKind);
        foreach (var (item, quantity) in new[] { (a, 30m), (b.Value, 10m) })
        {
            var partial = await SuggestAsync(client, new { constructionId = site.Construction, planItemIds = new[] { item } });
            Assert.Equal(quantity, Assert.Single(partial.GetProperty("lines").EnumerateArray()).GetProperty("quantity").GetDecimal());
            AssertWarnings(partial);
        }

        var draft = await ReadAsync(await client.PostAsJsonAsync(Requests, new
        {
            all.constructionId, all.planItemIds, lines = new[] { Line(material, 65.123456m, null) },
        }), HttpStatusCode.Created);
        Assert.Equal(65.123456m, Assert.Single(draft.GetProperty("lines").EnumerateArray()).GetProperty("quantity").GetDecimal());
        await WriteAsync(client, HttpMethod.Post, Address(draft) + "/submit", Version(draft), null, HttpStatusCode.OK);
        var repeated = await SuggestAsync(client, all);
        Assert.Equal(70m, Assert.Single(repeated.GetProperty("lines").EnumerateArray()).GetProperty("quantity").GetDecimal());
        AssertWarnings(repeated);
    }

    [Fact]
    public async Task Раздел_стройки_берёт_распределённую_долю_и_считает_весь_отпуск()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var other = await DistributionFlow.SectionAsync(host, site.Construction, "ЭОМ-2");
        var material = (await EstimateSeed.ClassifierAsync(host)).Nomenclature;
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = await EstimateSeed.RevisionAsync(host, estimate, await RowAsync(site, material, 100, "м"));
        var row = Assert.Single(await DistributionFlow.RowIdsAsync(host, revision.Id));
        using var editor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, PlanModule.EstimateEdit);
        await DistributionFlow.LinesAsync(editor, revision.Id,
            DistributionFlow.Line(row, "row", (site.Section, 0.4m), (other, 0.6m)));
        await IssueAsync(site.Construction, material, 30, "м");
        using var client = await SupplierAsync();

        var answer = await SuggestAsync(client, new { constructionId = site.Construction, sectionId = site.Section });
        Assert.Equal("constructionSection", answer.GetProperty("selection").GetString());
        AssertWarnings(answer);
        var line = Assert.Single(answer.GetProperty("lines").EnumerateArray());
        Assert.Equal(40m, line.GetProperty("estimateQuantity").GetDecimal());
        Assert.Equal(10m, line.GetProperty("quantity").GetDecimal());
        await ReadAsync(await client.PostAsJsonAsync(Suggest, new
        {
            constructionId = site.Construction, sectionId = site.Section, planItemIds = Array.Empty<Guid>(),
        }), HttpStatusCode.BadRequest);
        await ReadAsync(await client.PostAsJsonAsync(Suggest, new
        {
            constructionId = site.Construction, sectionId = Guid.NewGuid(),
        }), HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Две_неизвестные_единицы_сметы_не_объединяются(bool samePlanItem)
    {
        var (site, material, a, b) = await SeedAsync(60, samePlanItem ? null : 40, unit: null);
        if (samePlanItem)
        {
            var additional = await EstimateSeed.EstimateAsync(host, site.Construction, "Дополнительная", EstimateKind.Additional);
            await EstimateSeed.RevisionAsync(host, additional, await RowAsync(site, material, 40, null));
        }
        var itemIds = b is { } other ? new[] { a, other } : [a];
        await IssueAsync(site.Construction, material, 5, null);
        using var client = await SupplierAsync();
        var result = await SuggestAsync(client, new { constructionId = site.Construction, planItemIds = itemIds });

        var lines = result.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal([40m, 60m], lines.Select(line => line.GetProperty("quantity").GetDecimal()).Order());
        Assert.Equal(itemIds.Order(), lines.Select(line => line.GetProperty("planItemId").GetGuid()).Distinct().Order());
        Assert.All(lines, line =>
        {
            Assert.True(line.GetProperty("unitMismatch").GetBoolean());
            Assert.Equal(JsonValueKind.Null, line.GetProperty("compatibleIssuedQuantity").ValueKind);
        });
    }

    [Fact]
    public async Task Права_сметы_и_заявки_независимы()
    {
        var (site, material, item, _) = await SeedAsync(100);
        var scope = new { constructionId = site.Construction, planItemIds = new[] { item } };
        using var reader = await GrantedSignIn.WithPermissionsAsync(host, CostsModule.RequestRead);
        using var writer = await GrantedSignIn.WithPermissionsAsync(host, CostsModule.RequestEdit);
        using var supplier = await SupplierAsync();
        using var manager = await AccessProbe.SignInAsync(host, "ProjectManager", "plan26");

        var hidden = await ReadAsync(await writer.PostAsJsonAsync(Suggest, scope), HttpStatusCode.OK);
        Assert.Equal("hidden", hidden.GetProperty("state").GetString());
        Assert.Equal(PlanModule.EstimateMaterials, hidden.GetProperty("permission").GetString());
        Assert.False(hidden.TryGetProperty("suggestion", out _));
        var body = new { scope.constructionId, scope.planItemIds, lines = new[] { Line(material, 50, item) } };
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync(Requests, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(Suggest, scope)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync(Requests)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(Requests)).StatusCode);
        foreach (var client in new[] { supplier, manager })
        {
            var access = await AccessProbe.AccessAsync(client);
            Assert.Contains(CostsModule.RequestRead, access.Permissions);
            Assert.Contains(CostsModule.RequestEdit, access.Permissions);
            await SuggestAsync(client, scope);
        }
    }

    [Fact]
    public async Task Переходы_строки_и_удаление_проверяют_версию_и_состояние()
    {
        var (site, material, item, _) = await SeedAsync(100);
        using var author = await SupplierAsync();
        using var other = await SupplierAsync();
        using var reader = await GrantedSignIn.WithPermissionsAsync(host, CostsModule.RequestRead);
        var draft = await CreateAsync(author, site.Construction, item, material);
        var path = Address(draft);
        var body = new { lines = new[] { Line(material, 15, item) } };
        await WriteAsync(author, HttpMethod.Put, path + "/lines", null, body, HttpStatusCode.BadRequest);
        await WriteAsync(author, HttpMethod.Post, path + "/submit", "*", null, HttpStatusCode.BadRequest);
        await WriteAsync(reader, HttpMethod.Put, path + "/lines", Version(draft), body, HttpStatusCode.Forbidden);
        var updated = await WriteAsync(author, HttpMethod.Put, path + "/lines", Version(draft), body, HttpStatusCode.OK);
        Assert.Equal(15m, Assert.Single(updated.GetProperty("lines").EnumerateArray()).GetProperty("quantity").GetDecimal());
        await WriteAsync(author, HttpMethod.Post, path + "/submit", Version(draft), null, HttpStatusCode.Conflict);
        var submitted = await WriteAsync(author, HttpMethod.Post, path + "/submit", Version(updated), null, HttpStatusCode.OK);
        Assert.Equal("submitted", submitted.GetProperty("status").GetString());
        await WriteAsync(author, HttpMethod.Put, path + "/lines", Version(submitted), body, HttpStatusCode.Conflict);
        await WriteAsync(author, HttpMethod.Delete, path, Version(submitted), null, HttpStatusCode.Conflict);
        var reason = new { reason = "Уточнить объём" };
        await WriteAsync(other, HttpMethod.Post, path + "/return", Version(submitted), reason, HttpStatusCode.Forbidden);
        await WriteAsync(author, HttpMethod.Post, path + "/return", Version(submitted), new { reason = " " }, HttpStatusCode.BadRequest);
        var returned = await WriteAsync(author, HttpMethod.Post, path + "/return", Version(submitted), reason, HttpStatusCode.OK);
        Assert.Equal("draft", returned.GetProperty("status").GetString());
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var target = draft.GetProperty("id").GetString();
            var log = await db.ActivityRecords.SingleAsync(row => row.TargetId == target && row.Action == "costs.request.returned");
            Assert.Equal(reason.reason, log.After);
        }
        var resubmitted = await WriteAsync(author, HttpMethod.Post, path + "/submit", Version(returned), null, HttpStatusCode.OK);
        var closed = await WriteAsync(author, HttpMethod.Post, path + "/close", Version(resubmitted), null, HttpStatusCode.OK);
        Assert.Equal("closed", closed.GetProperty("status").GetString());
        await WriteAsync(author, HttpMethod.Post, path + "/return", Version(closed), reason, HttpStatusCode.Conflict);
        var disposable = await CreateAsync(author, site.Construction, item, material);
        Assert.True(disposable.GetProperty("number").GetInt64() > draft.GetProperty("number").GetInt64());
        await WriteAsync(author, HttpMethod.Delete, Address(disposable), Version(disposable), null, HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync(Address(disposable))).StatusCode);
        var list = await reader.GetFromJsonAsync<JsonElement>($"{Requests}?constructionId={site.Construction}");
        Assert.Equal(Version(closed), Version(Assert.Single(list.GetProperty("items").EnumerateArray())));
    }

    [Fact]
    public async Task Номенклатуру_занятую_строкой_заявки_нельзя_удалить()
    {
        var (site, original, item, _) = await SeedAsync(100);
        using var scope = host.Services.CreateScope();
        var app = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var type = await app.DomainObjects.Where(row => row.Id == original).Select(row => row.CompositeTypeId).SingleAsync();
        var material = (await new WorkTypeSeed(host).RecordAsync(type, "Отдельная позиция для заявки")).Id;
        using var supplier = await SupplierAsync();
        var draft = await CreateAsync(supplier, site.Construction, item, material);
        using var admin = await AccessProbe.SignInAsync(host, "Admin", "plan26");

        var refusal = await ReadAsync(await admin.DeleteAsync($"/api/common-data/{material}"), HttpStatusCode.Conflict);
        Assert.Contains("строки заявок на закупку с этой позицией номенклатуры — 1", refusal.GetProperty("error").GetString());
        await WriteAsync(supplier, HttpMethod.Delete, Address(draft), Version(draft), null, HttpStatusCode.NoContent);
        await ReadAsync(await admin.DeleteAsync($"/api/common-data/{material}"), HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Параллельные_замены_с_одной_версией_оставляют_ровно_один_набор_строк()
    {
        var (site, material, item, _) = await SeedAsync(100);
        using var supplier = await SupplierAsync();
        var draft = await CreateAsync(supplier, site.Construction, item, material);
        var path = Address(draft);

        async Task<HttpStatusCode> ReplaceAsync(decimal quantity)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, path + "/lines")
            {
                Content = JsonContent.Create(new { lines = new[] { Line(material, quantity, item) } }),
            };
            request.Headers.TryAddWithoutValidation("If-Match", Version(draft));
            using var response = await supplier.SendAsync(request);
            return response.StatusCode;
        }

        var results = await Task.WhenAll(ReplaceAsync(10), ReplaceAsync(20));
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], results.Order());
        var saved = await supplier.GetFromJsonAsync<JsonElement>(path);
        var line = Assert.Single(saved.GetProperty("lines").EnumerateArray());
        Assert.Equal(results[0] == HttpStatusCode.OK ? 10m : 20m, line.GetProperty("quantity").GetDecimal());
    }

    [Fact]
    public async Task Восстановление_возвращает_строки_связь_со_счётом_и_продолжает_нумерацию()
    {
        var (site, material, item, _) = await SeedAsync(100);
        using var scope = host.Services.CreateScope();
        using var supplier = await SupplierAsync();
        var draft = await CreateAsync(supplier, site.Construction, item, material);
        var requestId = draft.GetProperty("id").GetGuid();
        var submitted = await WriteAsync(supplier, HttpMethod.Post, Address(draft) + "/submit",
            Version(draft), null, HttpStatusCode.OK);
        var invoice = await AllocatedInvoiceAsync(supplier, site.Construction, material, 12);
        var linked = await LinkInvoiceAsync(supplier, invoice, requestId, HttpStatusCode.OK);
        var invoiceId = linked.GetProperty("id").GetGuid();
        var returned = await WriteAsync(supplier, HttpMethod.Post, Address(draft) + "/return",
            Version(submitted), new { reason = "Дополнить заявку" }, HttpStatusCode.OK);

        await WriteAsync(supplier, HttpMethod.Delete, Address(draft), Version(returned), null, HttpStatusCode.Conflict);
        var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
        var (export, _) = await backup.ExportAsync(BackupScope.Full);
        await using var archive = export;

        using (var reset = host.Services.CreateScope())
        {
            var db = reset.ServiceProvider.GetRequiredService<CostsDbContext>();
            await db.Invoices.Where(invoice => invoice.Id == invoiceId).ExecuteDeleteAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER SEQUENCE costs.purchase_requests_number_seq RESTART WITH 1");
        }
        await WriteAsync(supplier, HttpMethod.Delete, Address(draft), Version(returned), null, HttpStatusCode.NoContent);
        using (var restore = host.Services.CreateScope())
        {
            var report = await restore.ServiceProvider.GetRequiredService<BackupService>().ImportAsync(archive);
            Assert.True(report.Success, string.Join("; ", report.Warnings));
            var db = restore.ServiceProvider.GetRequiredService<CostsDbContext>();
            var restoredInvoice = await db.Invoices.AsNoTracking().SingleAsync(row => row.Id == invoiceId);
            Assert.Equal(requestId, restoredInvoice.PurchaseRequestId);
        }

        var restored = await supplier.GetFromJsonAsync<JsonElement>(Address(draft));
        Assert.Equal(draft.GetProperty("number").GetInt64(), restored.GetProperty("number").GetInt64());
        Assert.True(JsonElement.DeepEquals(draft.GetProperty("lines"), restored.GetProperty("lines")));
        var next = await CreateAsync(supplier, site.Construction, item, material);
        Assert.True(next.GetProperty("number").GetInt64() > restored.GetProperty("number").GetInt64());
    }

    private async Task<(EstimateSeed.Site Site, Guid Material, Guid A, Guid? B)> SeedAsync(
        decimal first, decimal? second = null, string? unit = "м")
    {
        var site = await EstimateSeed.SiteAsync(host);
        var material = (await EstimateSeed.ClassifierAsync(host)).Nomenclature;
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var row = await RowAsync(site, material, first, unit);
        if (second is null)
        {
            await EstimateSeed.RevisionAsync(host, estimate, row);
            return (site, material, row.PlanItemId!.Value, null);
        }
        var other = await DistributionFlow.SectionAsync(host, site.Construction, "ЭОМ-2");
        var another = await RowAsync(site with { Section = other }, material, second.Value, unit);
        await EstimateSeed.RevisionAsync(host, estimate, row, another);
        return (site, material, row.PlanItemId!.Value, another.PlanItemId);
    }

    private async Task<EstimateRowDraft> RowAsync(EstimateSeed.Site site, Guid material, decimal quantity, string? unit) =>
        (await EstimateSeed.MatchedRowAsync(host, site, 100m)) with
        {
            Materials = [new EstimateMaterialDraft("Кабель", quantity, SourceUnit: unit, NomenclatureId: material)],
        };

    private async Task IssueAsync(Guid site, Guid? material, decimal quantity, string? unit, bool posted = true)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
        var date = new DateOnly(2026, 11, 20);
        var bill = Waybill.Create(null);
        bill.Apply(new WaybillHeader(null, date, null, site, null, null));
        if (posted) bill.Post(null, date);
        var line = WaybillLine.Create(bill.Id);
        line.Apply(1, new WaybillLineValues(material, "Кабель", unit, quantity, null));
        db.Waybills.Add(bill);
        db.WaybillLines.Add(line);
        await db.SaveChangesAsync();
    }

    private Task<HttpClient> SupplierAsync() => AccessProbe.SignInAsync(host, "Supplier", "plan26");

    private static async Task<JsonElement> SuggestAsync(HttpClient client, object body)
    {
        var result = await ReadAsync(await client.PostAsJsonAsync(Suggest, body), HttpStatusCode.OK);
        Assert.Equal("shown", result.GetProperty("state").GetString());
        return result.GetProperty("suggestion");
    }

    internal static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        using (response)
        {
            var content = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == status, $"Ожидался {status}, получен {response.StatusCode}: {content}");
            if (status == HttpStatusCode.NoContent) return default;
            if (!response.IsSuccessStatusCode && content.Length == 0) return default;
            return JsonSerializer.Deserialize<JsonElement>(content);
        }
    }

    private static async Task<JsonElement> WriteAsync(
        HttpClient client, HttpMethod method, string path, string? version, object? body, HttpStatusCode status)
    {
        using var request = new HttpRequestMessage(method, path);
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await ReadAsync(await client.SendAsync(request), status);
    }

    private static Task<JsonElement> CreateAsync(HttpClient client, Guid constructionId, Guid item, Guid material) =>
        CreateAsync(client, new { constructionId, planItemIds = new[] { item }, lines = new[] { Line(material, 50, item) } });

    private static async Task<JsonElement> CreateAsync(HttpClient client, object body) =>
        await ReadAsync(await client.PostAsJsonAsync(Requests, body), HttpStatusCode.Created);

    private static object Line(Guid nomenclatureId, decimal quantity, Guid? planItemId) =>
        new { nomenclatureId, quantity, planItemId, unit = "м" };

    private static string Address(JsonElement request) => $"{Requests}/{request.GetProperty("id").GetGuid()}";
    private static string Version(JsonElement request) => request.GetProperty("version").GetString()!;

    private static void AssertWarnings(JsonElement suggestion)
    {
        var warnings = suggestion.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()!).ToArray();
        Assert.Contains(warnings, warning => warning.Contains("по всей стройке", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("Смета ограничена выбором", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("не резервируют", StringComparison.Ordinal));
    }
}

public sealed class PurchaseRequestHost : ModuleOnlyHost
{
    protected override string ModuleCode => "costs,plan";
    protected override string DatabaseSuffix => "plan26";
}
