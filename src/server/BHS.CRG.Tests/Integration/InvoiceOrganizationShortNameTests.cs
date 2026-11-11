using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Счета называют поставщика сокращённым названием, если оно заполнено (ТЗ COST-6, issue #1473): выбор
/// организации отдаёт полное рядом (В1), а отбор реестра по названию находит счёт и по видимому, и по
/// полному (В2). Справочник — общий «Организация» тестовой базы: сокращённое у него — своё поле с тэгом
/// <c>org.shortName</c>, которое тест ставит, если его ещё нет.
/// </summary>
public class InvoiceOrganizationShortNameTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private const string ShortKey = "КраткоеНазвание";

    [Fact]
    public async Task Выбор_отдаёт_полное_рядом_а_реестр_показывает_сокращённое_и_находит_по_обоим()
    {
        var mark = Guid.NewGuid().ToString("N")[..6];
        var full = $"Общество с ограниченной ответственностью «Ромашка {mark}»";
        var shortName = $"ООО «Ромашка {mark}»";
        var organization = await OrganizationAsync(full, shortName);
        var (admin, _) = await SignInAsync("Admin");

        var choice = await admin.GetFromJsonAsync<JsonElement>("/api/costs/organizations?purpose=choice");
        var item = choice.EnumerateArray().Single(o => o.GetProperty("id").GetGuid() == organization);
        Assert.Equal(shortName, item.GetProperty("name").GetString());
        Assert.Equal(full, item.GetProperty("fullName").GetString());

        var invoice = await InvoiceAsync(admin, organization);

        Assert.Equal(shortName, await SupplierCellAsync(admin, invoice));
        Assert.True(await FoundAsync(admin, invoice, "contains", $"ООО «Ромашка {mark}"), "по сокращённому");
        Assert.True(await FoundAsync(admin, invoice, "contains", $"ответственностью «Ромашка {mark}"), "по полному");
        // Отрицание — по обоим сразу: подходит полное, значит, под «не содержит» счёт не попадает.
        Assert.False(await FoundAsync(admin, invoice, "not_contains", $"ответственностью «Ромашка {mark}"), "«не» по полному");
        Assert.False(await FoundAsync(admin, invoice, "not_contains", $"ООО «Ромашка {mark}"), "«не» по сокращённому");
        Assert.True(await FoundAsync(admin, invoice, "not_contains", $"Лютик {mark}"), "«не» по чужому слову");
    }

    /// <summary>Организация с сокращённым названием своим полем; тэг на поле ставится один раз.</summary>
    private async Task<Guid> OrganizationAsync(string full, string shortName)
    {
        var typeId = await TypeAsync(TestDirectories.OrganizationCode, "Организация");
        using (var scope = host.Services.CreateScope())
        {
            var types = scope.ServiceProvider.GetRequiredService<IRepository<DocumentType>>();
            var type = (await types.GetByIdAsync(typeId))!;
            var schema = JsonNode.Parse(type.Schema.RootElement.GetRawText())!.AsObject();
            var fields = schema["fields"] as JsonArray ?? [];
            if (!fields.OfType<JsonObject>().Any(f => f["tags"] is JsonArray tags
                    && tags.Any(t => t?.GetValue<string>() == FunctionalTag.OrgShortName)))
            {
                fields.Add(new JsonObject
                {
                    ["key"] = ShortKey, ["type"] = "string", ["title"] = "Краткое название",
                    ["tags"] = new JsonArray(FunctionalTag.OrgShortName),
                });
                schema["fields"] = fields;
                type.UpdateSchema(JsonDocument.Parse(schema.ToJsonString()));
                types.Update(type);
                await types.SaveChangesAsync();
            }
        }

        using var write = host.Services.CreateScope();
        var mediator = write.ServiceProvider.GetRequiredService<MediatR.IMediator>();
        var created = await mediator.Send(new BHS.CRG.Application.Documents.CreateCommonDataEntryCommand(full, typeId,
            JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { [ShortKey] = shortName })),
            BHS.CRG.Domain.Catalog.CatalogScope.System, null));
        return created.Id;
    }

    private static async Task<(Guid Id, string Number)> InvoiceAsync(HttpClient client, Guid organization)
    {
        var number = $"СЧ-{Guid.NewGuid().ToString()[..6]}";
        var requisites = new Dictionary<string, object?>
        {
            ["Номер"] = number,
            ["Дата"] = "2026-11-11",
            ["Поставщик"] = Reference(organization),
        };
        var response = await client.PostAsJsonAsync("/api/costs/invoices", new { requisites });
        await OkAsync(response);
        return ((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(), number);
    }

    /// <summary>
    /// Попал ли счёт под условие по поставщику. Вместе с номером: на общей базе «не содержит» отдаёт
    /// чужие счета страницами, и свой ушёл бы за первую.
    /// </summary>
    private static async Task<bool> FoundAsync(HttpClient client, (Guid Id, string Number) invoice, string op, string value)
    {
        var filter = JsonSerializer.Serialize(new
        {
            type = "group", logic = "and",
            children = new object[]
            {
                new { type = "condition", column = "Номер", op = "eq", value = invoice.Number },
                new { type = "condition", column = "Поставщик", op, value },
            },
        });
        var response = await client.GetAsync(
            $"/api/tables/costs.invoices?columns=Номер,Поставщик&filter={Uri.EscapeDataString(filter)}");
        await OkAsync(response);
        var table = await response.Content.ReadFromJsonAsync<JsonElement>();
        return table.GetProperty("keys").EnumerateArray().Any(k => k.GetString() == invoice.Id.ToString());
    }

    private static async Task<string?> SupplierCellAsync(HttpClient client, (Guid Id, string Number) invoice)
    {
        var response = await client.GetAsync($"/api/tables/costs.invoices?columns=Номер,Поставщик&row={invoice.Id}");
        await OkAsync(response);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rows")[0].GetProperty("Поставщик").GetString();
    }
}
