using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;

namespace BHS.CRG.Tests.Plan;

[Collection(PlanOnlyCollection.Name)]
public class OfferPrintTests(PlanOnlyHost host)
{
    [Theory]
    [InlineData("none", null, null, 1200)]
    [InlineData("included", 20, 200, 1200)]
    [InlineData("on-top", 20, 240, 1440)]
    public async Task Print_uses_saved_sale_prices_vat_and_version_without_internal_fields(
        string vatMode, int? vatRate, int? vatAmount, int total)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var recipient = await SeedRecipientAsync();
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = await EstimateSeed.RevisionAsync(host, estimate,
            new EstimateRowDraft("Монтаж", SourceVolume: 3m, SourceUnit: "шт", Total: 1000m));
        var manager = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferEdit, PlanModule.OfferSend,
            PlanModule.OfferRead, PlanModule.EstimateCost, CorePermissions.CatalogRead);
        var created = await manager.PostAsJsonAsync("/api/plan/offers", new
        {
            revisionId = revision.Id, date = "2026-11-12", markup = 20m, vatMode, vatRate,
            data = new Dictionary<string, object>
            {
                ["Примечание"] = "Работы за три дня",
                ["Получатель"] = new Dictionary<string, object> { ["$ref"] = "catalog", ["entryId"] = recipient },
                ["ГрафикОплаты"] = new Dictionary<string, decimal> { ["Итого"] = 725m },
                ["nested"] = new { СЕБЕСТОИМОСТЬ = 1000m, markUp = 20m },
            },
        });
        created.EnsureSuccessStatusCode();
        var offer = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(20m, offer.GetProperty("markup").GetDecimal());
        Assert.Equal(1000m, offer.GetProperty("lines")[0].GetProperty("cost").GetProperty("total").GetDecimal());

        var id = offer.GetProperty("id").GetGuid();
        var sent = await UpdateAsync(manager, offer, "/send", new { sentOn = "2026-11-13" });
        var path = $"/api/print/{PlanRecordTypes.OfferCode}/{id}";
        var missing = await manager.PostAsync($"/api/print/{PlanRecordTypes.OfferCode}/{Guid.NewGuid()}", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var noPrint = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateCost);
        Assert.Equal(HttpStatusCode.Forbidden, (await noPrint.PostAsync(path, null)).StatusCode);

        var print = await manager.PostAsync(path, null);
        print.EnsureSuccessStatusCode();
        Assert.Equal("application/pdf", print.Content.Headers.ContentType?.MediaType);
        var capture = Assert.IsType<PlanOnlyPrintStub>(host.Services.GetRequiredService<IDocumentGeneratorFactory>());
        var request = Assert.IsType<GenerationRequest>(capture.LastRequest);
        var context = JsonSerializer.SerializeToElement(request.Context.Data);
        var line = Assert.Single(context.GetProperty("lines").EnumerateArray());
        Assert.Equal((400m, 1200m), (line.GetProperty("price").GetDecimal(), line.GetProperty("amount").GetDecimal()));
        Assert.Equal((vatMode, (decimal?)vatRate), (context.GetProperty("vat").GetProperty("mode").GetString(),
            context.GetProperty("vat").GetProperty("rate").Deserialize<decimal?>()));
        Assert.Equal((decimal?)vatAmount, context.GetProperty("money").GetProperty("vatAmount").Deserialize<decimal?>());
        Assert.Equal(total, context.GetProperty("money").GetProperty("total").GetDecimal());
        Assert.Equal(offer.GetProperty("number").GetString(), context.GetProperty("number").GetString());
        Assert.Equal(1, context.GetProperty("version").GetInt32());
        Assert.Equal("2026-11-13", context.GetProperty("versionDate").GetString());
        Assert.Equal("Работы за три дня", context.GetProperty("conditions").GetProperty("Примечание").GetString());
        Assert.Equal("Монтажная компания", context.GetProperty("conditions").GetProperty("Получатель").GetProperty("Название").GetString());
        Assert.Equal(725m, context.GetProperty("conditions").GetProperty("ГрафикОплаты").GetProperty("Итого").GetDecimal());
        Assert.DoesNotContain(Keys(context), k => new[] { "cost", "markup", "margin", "себестоимость", "наценка" }
            .Contains(k, StringComparer.OrdinalIgnoreCase));

        // Компилируем ровно тот запрос, который подмена приняла на установке только с plan.
        var pdf = await host.Services.GetRequiredService<TypstGenerator>().GenerateAsync(request);
        using var document = PdfDocument.Open(pdf);
        var printed = document.GetPage(1).Text;
        Assert.Contains("Монтаж", printed);
        Assert.Contains("13.11.2026", printed);
        Assert.Contains(total.ToString(System.Globalization.CultureInfo.InvariantCulture), printed);

        if (vatMode == "on-top")
        {
            var editor = await GrantedSignIn.WithPermissionsAsync(host, CorePermissions.PrintEdit);
            var types = await editor.GetFromJsonAsync<JsonElement>("/api/templates/types");
            var typeId = Assert.Single(types.EnumerateArray()).GetProperty("type").GetProperty("id").GetGuid();
            // Фрагменты, которые вставляет кнопка «Реквизит», в том числе поле заказчика.
            var edited = request.TemplateContent + """

                Проверочный итог #{ import "systemlib.typ": print-field; print-field(("money", "total",)) }

                Проверочное условие #{ import "systemlib.typ": print-field; print-field(("conditions", "Примечание",)) }

                Получатель из справочника #{ import "systemlib.typ": print-field; print-field(("conditions", "Получатель", "Название",)) }

                Сумма графика #{ import "systemlib.typ": print-field; print-field(("conditions", "ГрафикОплаты", "Итого",)) }
                """;
            var createdTemplate = await editor.PostAsJsonAsync("/api/templates", new
            {
                documentTypeId = typeId, name = "Шаблон с вставленными реквизитами", content = edited,
            });
            createdTemplate.EnsureSuccessStatusCode();
            var templateId = (await createdTemplate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            (await manager.PostAsync($"{path}?templateId={templateId}", null)).EnsureSuccessStatusCode();
            var editedPdf = await host.Services.GetRequiredService<TypstGenerator>().GenerateAsync(capture.LastRequest!);
            using var editedDocument = PdfDocument.Open(editedPdf);
            var text = string.Join(" ", editedDocument.GetPages().SelectMany(page => page.GetWords()).Select(word => word.Text));
            Assert.Matches(@"Проверочный\s+итог\s*1440", text);
            Assert.Matches(@"Проверочное\s+условие\s*Работы за три дня", text);
            Assert.Matches(@"Получатель\s+из\s+справочника\s+Монтажная компания", text);
            Assert.Matches(@"Сумма\s+графика\s+725", text);

            var withoutCatalog = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.OfferRead);
            (await withoutCatalog.PostAsync(path, null)).EnsureSuccessStatusCode();
            var restricted = JsonSerializer.SerializeToElement(capture.LastRequest!.Context.Data);
            Assert.Equal(JsonValueKind.Null, restricted.GetProperty("conditions").GetProperty("Получатель").ValueKind);
            Assert.Equal(725m, restricted.GetProperty("conditions").GetProperty("ГрафикОплаты").GetProperty("Итого").GetDecimal());
        }

        await UpdateAsync(manager, sent, "/versions");
        (await manager.PostAsync(path + "/preview", null)).EnsureSuccessStatusCode();
        var draft = JsonSerializer.SerializeToElement(capture.LastRequest!.Context.Data);
        Assert.Equal(2, draft.GetProperty("version").GetInt32());
        Assert.Equal("2026-11-12", draft.GetProperty("versionDate").GetString());
    }

    private async Task<Guid> SeedRecipientAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        const string code = "ПолучательПечатнойФормыКП";
        var type = await db.DocumentTypes.SingleOrDefaultAsync(t => t.Code == code);
        if (type is null)
        {
            type = DocumentType.Create("Получатель печатной формы КП", code, DocumentTypeKind.Composite, null,
                JsonDocument.Parse("""{"fields":[{"key":"Название","type":"string"},{"key":"Наценка","type":"number"}]}"""),
                TypeOwner.Core, TypeVisibility.Shared);
            var schedule = DocumentType.Create("График оплаты КП", "ГрафикОплатыКП", DocumentTypeKind.Composite, null,
                JsonDocument.Parse("""{"fields":[{"key":"Итого","type":"number"}]}"""), TypeOwner.Core, TypeVisibility.Shared);
            db.DocumentTypes.AddRange(type, schedule);
            var offerType = await db.DocumentTypes.SingleAsync(t => t.Code == PlanRecordTypes.OfferCode);
            var schema = JsonNode.Parse(offerType.Schema.RootElement.GetRawText())!.AsObject();
            var fields = schema["fields"]!.AsArray();
            fields.Add(JsonSerializer.SerializeToNode(new { key = "Получатель", type = "complex", typeId = type.Id }));
            fields.Add(JsonSerializer.SerializeToNode(new { key = "ГрафикОплаты", type = "complex", typeId = schedule.Id }));
            offerType.UpdateSchema(JsonSerializer.SerializeToDocument(schema));
        }
        var parent = DomainObject.Create(type.Id, "Организация", JsonDocument.Parse("""{"Название":"Монтажная компания","Наценка":17}"""),
            CatalogScope.System, null);
        var recipient = DomainObject.Create(type.Id, "Получатель", JsonSerializer.SerializeToDocument(
            new { _baseRef = parent.Id }), CatalogScope.System, null);
        db.DomainObjects.AddRange(parent, recipient);
        await db.SaveChangesAsync();
        return recipient.Id;
    }

    private static async Task<JsonElement> UpdateAsync(HttpClient client, JsonElement offer, string suffix, object? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/plan/offers/{offer.GetProperty("id").GetGuid()}{suffix}");
        request.Headers.TryAddWithoutValidation("If-Match", offer.GetProperty("version").GetString());
        if (body is not null) request.Content = JsonContent.Create(body);
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static IEnumerable<string> Keys(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().SelectMany(p => Keys(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => value.EnumerateArray().SelectMany(Keys),
        _ => [],
    };
}
