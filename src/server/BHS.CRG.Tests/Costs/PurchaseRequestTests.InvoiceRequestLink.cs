using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Plan;
using BHS.CRG.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Costs;

public sealed partial class PurchaseRequestTests
{
    private const string Invoices = "/api/costs/invoices";
    private const HttpStatusCode Ineligible = HttpStatusCode.UnprocessableEntity;

    [Fact]
    public async Task InvoiceRequestLink_только_поданная_заявка_и_полная_разноска_на_её_стройку()
    {
        var (site, material, item, _) = await SeedAsync(100);
        var other = await EstimateSeed.SiteAsync(host);
        var secondSection = await DistributionFlow.SectionAsync(host, site.Construction, "Тот же объект");
        using var editor = await SupplierAsync();
        var draft = await CreateAsync(editor, site.Construction, item, material);
        var requestId = draft.GetProperty("id").GetGuid();
        var invoice = await DraftInvoiceAsync(editor, 100);
        invoice = await SetInvoiceLinesAsync(editor, invoice, InvoiceLine(material, 100, "м"));

        await LinkInvoiceAsync(editor, invoice, requestId, Ineligible);
        var submitted = await WriteAsync(editor, HttpMethod.Post, Address(draft) + "/submit",
            Version(draft), null, HttpStatusCode.OK);
        var linkBody = new { requisites = invoice.GetProperty("requisites"), purchaseRequestId = requestId };
        await WriteAsync(editor, HttpMethod.Put, InvoiceAddress(invoice), SeenInvoiceVersion.Omit, linkBody, HttpStatusCode.BadRequest);
        await WriteAsync(editor, HttpMethod.Put, InvoiceAddress(invoice), "0", linkBody, HttpStatusCode.Conflict);
        using var manager = await AccessProbe.SignInAsync(host, "ProjectManager", "plan26");
        await WriteAsync(manager, HttpMethod.Put, InvoiceAddress(invoice), Version(invoice), linkBody, HttpStatusCode.Forbidden);
        await LinkInvoiceAsync(editor, invoice, requestId, Ineligible);
        await LinkInvoiceAsync(editor, invoice, Guid.NewGuid(), Ineligible);

        invoice = await SetAllocationAsync(editor, invoice, HttpStatusCode.OK, new { construction = other.Construction, quantity = 100 });
        await LinkInvoiceAsync(editor, invoice, requestId, Ineligible);
        invoice = await SetAllocationAsync(editor, invoice, HttpStatusCode.OK, new { construction = site.Construction, quantity = 60 });
        await LinkInvoiceAsync(editor, invoice, requestId, Ineligible);
        invoice = await SetAllocationAsync(editor, invoice, HttpStatusCode.OK,
            new { construction = site.Construction, quantity = 60 }, new { construction = other.Construction, quantity = 40 });
        await LinkInvoiceAsync(editor, invoice, requestId, Ineligible);
        using var admin = await AccessProbe.SignInAsync(host, "Admin", "plan26");
        var article = await ReadAsync(await admin.PostAsJsonAsync("/api/costs/articles",
            new { name = $"Склад {Guid.NewGuid():N}" }), HttpStatusCode.OK);
        invoice = await SetAllocationAsync(editor, invoice, HttpStatusCode.OK, new { construction = site.Construction, quantity = 60 },
            new { article = article.GetProperty("id").GetGuid(), quantity = 40 });
        await LinkInvoiceAsync(editor, invoice, requestId, Ineligible);

        var firstPart = new { construction = site.Construction, section = site.Section, quantity = 60 };
        var secondPart = new { construction = site.Construction, section = secondSection, quantity = 40 };
        invoice = await SetAllocationAsync(editor, invoice, HttpStatusCode.OK, firstPart, secondPart);
        var linked = await LinkInvoiceAsync(editor, invoice, requestId, HttpStatusCode.OK);
        Assert.Equal(requestId, linked.GetProperty("purchaseRequestId").GetGuid());
        Assert.NotEqual(Version(invoice), Version(linked));

        // Отказ после попытки записи откатывает обе части разноски и версию счёта.
        await SetAllocationAsync(editor, linked, Ineligible, firstPart, new { construction = other.Construction, quantity = 40 });
        await WriteAsync(editor, HttpMethod.Put, InvoiceAddress(linked) + "/lines", Version(linked),
            new { lines = new[] { InvoiceLine(material, 110, "м", InvoiceLineId(linked)) } }, Ineligible);
        var unchanged = await editor.GetFromJsonAsync<JsonElement>(InvoiceAddress(linked));
        Assert.Equal(Version(linked), Version(unchanged));
        Assert.Equal(100m, unchanged.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());

        await WriteAsync(editor, HttpMethod.Post, Address(submitted) + "/close", Version(submitted), null, HttpStatusCode.OK);
        var kept = await WriteAsync(editor, HttpMethod.Put, InvoiceAddress(linked), Version(linked),
            new { requisites = linked.GetProperty("requisites") }, HttpStatusCode.OK);
        Assert.Equal(requestId, kept.GetProperty("purchaseRequestId").GetGuid());
        kept = await LinkInvoiceAsync(editor, kept, requestId, HttpStatusCode.OK);
        var unlinked = await LinkInvoiceAsync(editor, kept, null, HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, unlinked.GetProperty("purchaseRequestId").ValueKind);
        await LinkInvoiceAsync(editor, unlinked, requestId, Ineligible);
        await SetAllocationAsync(editor, unlinked, HttpStatusCode.OK, new { construction = other.Construction, quantity = 100 });
    }

    [Fact]
    public async Task InvoiceRequestLink_разноска_счёта_без_строк_и_защита_всей_матрицы()
    {
        var (site, material, item, _) = await SeedAsync(100);
        var other = await EstimateSeed.SiteAsync(host);
        using var editor = await SupplierAsync();
        var draft = await CreateAsync(editor, site.Construction, item, material);
        await WriteAsync(editor, HttpMethod.Post, Address(draft) + "/submit", Version(draft), null, HttpStatusCode.OK);
        var requestId = draft.GetProperty("id").GetGuid();
        var invoice = await DraftInvoiceAsync(editor, 100);

        async Task<JsonElement> AllocateDocument(JsonElement current, object[] document, HttpStatusCode status) =>
            await WriteAsync(editor, HttpMethod.Put, InvoiceAddress(current) + "/allocation", Version(current),
                new
                {
                    lines = Array.Empty<object>(), document,
                    stamp = current.GetProperty("allocation").GetProperty("stamp").GetString(),
                }, status);

        invoice = await AllocateDocument(invoice, [new { construction = site.Construction, amount = 60 }], HttpStatusCode.OK);
        await LinkInvoiceAsync(editor, invoice, requestId, Ineligible);
        invoice = await AllocateDocument(invoice, [new { construction = site.Construction, amount = 100 }], HttpStatusCode.OK);
        var linked = await LinkInvoiceAsync(editor, invoice, requestId, HttpStatusCode.OK);
        var withoutLines = UnitRow(await QuantitiesAsync(editor, draft), "м");
        Assert.Equal(JsonValueKind.Null, withoutLines.GetProperty("invoicedQuantity").ValueKind);
        var withLines = await AllocatedInvoiceAsync(editor, site.Construction, material, 12);
        await LinkInvoiceAsync(editor, withLines, requestId, HttpStatusCode.OK);
        var incomplete = UnitRow(await QuantitiesAsync(editor, draft), "м");
        Assert.Equal(JsonValueKind.Null, incomplete.GetProperty("invoicedQuantity").ValueKind);

        await AllocateDocument(linked, [new { construction = other.Construction, amount = 100 }], Ineligible);
        var stillLinked = await editor.GetFromJsonAsync<JsonElement>(InvoiceAddress(linked));
        Assert.Equal(Version(linked), Version(stillLinked));
        Assert.Equal(requestId, stillLinked.GetProperty("purchaseRequestId").GetGuid());
        await LinkInvoiceAsync(editor, stillLinked, null, HttpStatusCode.OK);
        var complete = UnitRow(await QuantitiesAsync(editor, draft), "м");
        Assert.Equal(12m, complete.GetProperty("invoicedQuantity").GetDecimal());
    }

    [Fact]
    public async Task InvoiceRequestLink_свод_по_единицам_считает_только_связанные_счета_и_учитывает_права()
    {
        var (site, material, item, _) = await SeedAsync(100);
        var other = await EstimateSeed.SiteAsync(host);
        using var editor = await SupplierAsync();
        var draft = await CreateAsync(editor, new
        {
            constructionId = site.Construction, planItemIds = new[] { item },
            lines = new object[]
            {
                Line(material, 40, item), Line(material, 60, item),
                new { nomenclatureId = material, quantity = 2, unit = "упак" },
            },
        });
        var requestId = draft.GetProperty("id").GetGuid();
        await WriteAsync(editor, HttpMethod.Post, Address(draft) + "/submit", Version(draft), null, HttpStatusCode.OK);
        var before = UnitRow(await QuantitiesAsync(editor, draft), "м");
        Assert.Equal(0m, before.GetProperty("invoicedQuantity").GetDecimal());
        Assert.Equal(0m, before.GetProperty("issuedForConstructionQuantity").GetDecimal());
        var first = await AllocatedInvoiceAsync(editor, site.Construction, material, 15);
        var second = await AllocatedInvoiceAsync(editor, site.Construction, material, 25);
        var wrongUnit = await AllocatedInvoiceAsync(editor, site.Construction, material, 1, "бухта");
        var unitMissing = await AllocatedInvoiceAsync(editor, site.Construction, material, 300, null);
        await AllocatedInvoiceAsync(editor, site.Construction, material, 500);
        foreach (var invoice in new[] { first, second, wrongUnit, unitMissing })
            await LinkInvoiceAsync(editor, invoice, requestId, HttpStatusCode.OK);

        await IssueAsync(site.Construction, material, 34, "м");
        await IssueAsync(site.Construction, material, 1, "бухта");
        await IssueAsync(site.Construction, material, 400, null);
        await IssueAsync(site.Construction, material, 200, "м", posted: false);
        await IssueAsync(other.Construction, material, 500, "м");

        var supplierView = await QuantitiesAsync(editor, draft);
        Assert.Equal("shown", supplierView.GetProperty("invoiced").GetProperty("state").GetString());
        Assert.Equal("shown", supplierView.GetProperty("issuedForConstruction").GetProperty("state").GetString());
        var metres = UnitRow(supplierView, "м");
        Assert.Equal(100m, metres.GetProperty("orderedQuantity").GetDecimal());
        Assert.Equal(40m, metres.GetProperty("invoicedQuantity").GetDecimal());
        Assert.Equal(34m, metres.GetProperty("issuedForConstructionQuantity").GetDecimal());
        Assert.True(metres.GetProperty("invoicedUnitMismatch").GetBoolean());
        Assert.True(metres.GetProperty("issuedForConstructionUnitMismatch").GetBoolean());
        var incompatible = UnitRow(supplierView, "упак");
        Assert.Equal(JsonValueKind.Null, incompatible.GetProperty("invoicedQuantity").ValueKind);
        Assert.Equal(JsonValueKind.Null, incompatible.GetProperty("issuedForConstructionQuantity").ValueKind);
        Assert.True(incompatible.GetProperty("invoicedUnitMismatch").GetBoolean());
        Assert.True(incompatible.GetProperty("issuedForConstructionUnitMismatch").GetBoolean());

        using var manager = await AccessProbe.SignInAsync(host, "ProjectManager", "plan26");
        var managerView = await QuantitiesAsync(manager, draft);
        AssertHidden(managerView, "invoiced", "costs.invoice.read");
        var managerMetres = UnitRow(managerView, "м");
        Assert.Equal(JsonValueKind.Null, managerMetres.GetProperty("invoicedQuantity").ValueKind);
        Assert.Equal(JsonValueKind.Null, managerMetres.GetProperty("invoicedUnitMismatch").ValueKind);
        Assert.Equal(34m, managerMetres.GetProperty("issuedForConstructionQuantity").GetDecimal());
        await ReadAsync(await manager.GetAsync(InvoiceAddress(first)), HttpStatusCode.Forbidden);
        await ReadAsync(await manager.GetAsync("/api/costs/waybills"), HttpStatusCode.Forbidden);

        using var readOnly = await GrantedSignIn.WithPermissionsAsync(host, CostsModule.RequestRead, "costs.invoice.read");
        var readView = await QuantitiesAsync(readOnly, draft);
        AssertHidden(readView, "issuedForConstruction", CostsModule.MaterialsRead);
        Assert.Equal(40m, UnitRow(readView, "м").GetProperty("invoicedQuantity").GetDecimal());
        Assert.Equal(JsonValueKind.Null, UnitRow(readView, "м").GetProperty("issuedForConstructionQuantity").ValueKind);

        var amountOnly = await DraftInvoiceAsync(editor, 10);
        amountOnly = await SetInvoiceLinesAsync(editor, amountOnly, new
        {
            nomenclature = new Dictionary<string, object> { ["$ref"] = "catalog", ["entryId"] = material },
            unit = "м", amount = 10,
        });
        amountOnly = await SetAllocationAsync(editor, amountOnly, HttpStatusCode.OK,
            new { construction = site.Construction, amount = 10 });
        await LinkInvoiceAsync(editor, amountOnly, requestId, HttpStatusCode.OK);
        var incomplete = UnitRow(await QuantitiesAsync(editor, draft), "м");
        Assert.Equal(JsonValueKind.Null, incomplete.GetProperty("invoicedQuantity").ValueKind);
    }

    private async Task<JsonElement> DraftInvoiceAsync(HttpClient client, decimal total)
    {
        using var scope = host.Services.CreateScope();
        var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
        if ((await types.FindAsync(type => type.Code == CostsRecordTypes.InvoiceCode)).Count == 0)
        {
            if ((await types.FindAsync(type => type.Code == TestDirectories.OrganizationCode)).Count == 0)
                await new WorkTypeSeed(host).TypeAsync(TestDirectories.OrganizationCode, "Организация");
            await TestDirectories.AssignRolesAsync(scope.ServiceProvider);
            await scope.ServiceProvider.ProjectModuleTypesAsync();
        }
        return await ReadAsync(await client.PostAsJsonAsync(Invoices,
            new { requisites = new Dictionary<string, object> { ["Номер"] = Guid.NewGuid().ToString("N"), ["Итого"] = total } }),
            HttpStatusCode.Created);
    }

    private async Task<JsonElement> AllocatedInvoiceAsync(
        HttpClient client, Guid construction, Guid material, decimal quantity, string? unit = "м")
    {
        var invoice = await DraftInvoiceAsync(client, quantity);
        invoice = await SetInvoiceLinesAsync(client, invoice, InvoiceLine(material, quantity, unit));
        return await SetAllocationAsync(client, invoice, HttpStatusCode.OK, new { construction, quantity });
    }

    private static Task<JsonElement> LinkInvoiceAsync(
        HttpClient client, JsonElement invoice, Guid? requestId, HttpStatusCode status) =>
        WriteAsync(client, HttpMethod.Put, InvoiceAddress(invoice), Version(invoice),
            new { requisites = invoice.GetProperty("requisites"), purchaseRequestId = requestId }, status);

    private static Task<JsonElement> SetInvoiceLinesAsync(HttpClient client, JsonElement invoice, params object[] lines) =>
        WriteAsync(client, HttpMethod.Put, InvoiceAddress(invoice) + "/lines", Version(invoice),
            new { lines }, HttpStatusCode.OK);

    private static Task<JsonElement> SetAllocationAsync(
        HttpClient client, JsonElement invoice, HttpStatusCode status, params object[] parts) =>
        WriteAsync(client, HttpMethod.Put, $"{InvoiceAddress(invoice)}/lines/{InvoiceLineId(invoice)}/allocation",
            Version(invoice), new { parts }, status);

    private static object InvoiceLine(Guid material, decimal quantity, string? unit, Guid? id = null) =>
        new
        {
            id, supplierText = "Кабель", nomenclature = new Dictionary<string, object> { ["$ref"] = "catalog", ["entryId"] = material },
            quantity, unit, price = 1,
        };

    private static string InvoiceAddress(JsonElement invoice) => $"{Invoices}/{invoice.GetProperty("id").GetGuid()}";
    private static Guid InvoiceLineId(JsonElement invoice) => invoice.GetProperty("lines")[0].GetProperty("id").GetGuid();

    private static async Task<JsonElement> QuantitiesAsync(HttpClient client, JsonElement request) =>
        (await client.GetFromJsonAsync<JsonElement>(Address(request))).GetProperty("quantities");

    private static JsonElement UnitRow(JsonElement quantities, string unit) =>
        Assert.Single(quantities.GetProperty("items").EnumerateArray(), row => row.GetProperty("unit").GetString() == unit);

    private static void AssertHidden(JsonElement quantities, string field, string permission)
    {
        var access = quantities.GetProperty(field);
        Assert.Equal("hidden", access.GetProperty("state").GetString());
        Assert.Contains(permission, access.GetProperty("anyOfPermissions").EnumerateArray().Select(item => item.GetString()));
    }
}
