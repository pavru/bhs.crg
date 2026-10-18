using System.Net.Http.Json;
using System.Text.Json;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Подсказка «похоже, цены без НДС» в готовности счёта (issue #1500): расхождение, которое объясняет режим
/// НДС, названо признаком <c>vatExplains</c> и словами — а режим сам не меняется никогда.
/// </summary>
// ⚠️ Собрание указывается КАЖДОМУ классу: атрибут не наследуется от базового.
[Collection("Integration")]
public class InvoiceVatModeGuessTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private static async Task HeaderAsync(HttpClient client, Guid invoice, string key, decimal value) =>
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, key, value) }));

    private static async Task<JsonElement> TotalGapAsync(HttpClient client, Guid invoice) =>
        Assert.Single((await ReadAsync(client, invoice)).GetProperty("readiness").GetProperty("gaps").EnumerateArray(),
            g => g.GetProperty("kind").GetString() == "total");

    private static string? Explains(JsonElement gap) => gap.GetProperty("vatExplains").GetString();

    /// <summary>
    /// Пример владельца, введённый «в том числе»: строки 1000 + 500, к оплате 1830, НДС 330 — расхождение
    /// ровно на НДС. Подсказка есть, отказ «Разобран» называет её словами, а режим остался прежним.
    /// </summary>
    [Fact]
    public async Task В_том_числе_с_расхождением_ровно_на_НДС_похоже_сверху()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(cable, 1, 1000, 22m, amount: 1000m), Line(conduit, 1, 500, 22m, amount: 500m)]);
        await HeaderAsync(client, invoice, "ВТомЧислеНДС", 330m);
        await HeaderAsync(client, invoice, "Итого", 1830m);

        var gap = await TotalGapAsync(client, invoice);
        Assert.Equal("onTop", Explains(gap));
        Assert.Contains("похоже, цены без НДС", gap.GetProperty("label").GetString());

        var parsed = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        var text = await parsed.Content.ReadAsStringAsync();
        Assert.Contains("ровно на НДС", text);
        Assert.Contains("отметьте «Цены без НДС, НДС сверху»", text);
        Assert.DoesNotContain("строка, которой нет в таблице", text);

        // Подсказка, не решение: ни режим, ни суммы строк догадка не тронула.
        var read = await ReadAsync(client, invoice);
        Assert.Equal("Included", read.GetProperty("vatMode").GetString());
        Assert.Equal(1000m, read.GetProperty("lines")[0].GetProperty("amount").GetDecimal());
    }

    /// <summary>
    /// Обратный случай: флажок «сверху» поставлен зря — бумажные суммы уже сходятся с суммой к оплате, а
    /// НДС, прибавленный по ставкам, уводит сумму строк выше неё ровно на НДС.
    /// </summary>
    [Fact]
    public async Task Сверху_зря_похоже_цены_уже_с_НДС()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new Dictionary<string, object?>
        {
            ["vatMode"] = "OnTop",
            ["lines"] = new object[] { OnTop(cable, 1000m, 22m), OnTop(conduit, 500m, 22m) },
        }));
        await HeaderAsync(client, invoice, "Итого", 1500m);

        var gap = await TotalGapAsync(client, invoice);
        Assert.Equal("included", Explains(gap));
        Assert.Contains("похоже, цены уже с НДС", gap.GetProperty("label").GetString());

        var text = await (await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null)).Content.ReadAsStringAsync();
        Assert.Contains("снимите отметку «Цены без НДС, НДС сверху»", text);
        Assert.Equal("OnTop", (await ReadAsync(client, invoice)).GetProperty("vatMode").GetString());
    }

    /// <summary>Расхождение, которое НДС не объясняет (скидка), — без признака и с прежним советом.</summary>
    [Fact]
    public async Task Расхождение_не_от_НДС_без_подсказки()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(cable, 1, 1000, 22m, amount: 1000m), Line(conduit, 1, 500, 22m, amount: 500m)]);
        await HeaderAsync(client, invoice, "ВТомЧислеНДС", 230m);
        await HeaderAsync(client, invoice, "Итого", 1400m);

        var gap = await TotalGapAsync(client, invoice);
        Assert.Equal(JsonValueKind.Null, gap.GetProperty("vatExplains").ValueKind);
        Assert.DoesNotContain("похоже", gap.GetProperty("label").GetString());

        var text = await (await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null)).Content.ReadAsStringAsync();
        Assert.Contains("строка, которой нет в таблице", text);
    }

    /// <summary>Строка бумаги «сверху»: сумма без НДС — полем netAmount.</summary>
    private static Dictionary<string, object?> OnTop(Guid nomenclature, decimal net, decimal rate)
    {
        var line = Line(nomenclature, 1, net, rate);
        line.Remove("amount");
        line["netAmount"] = net;
        return line;
    }
}
