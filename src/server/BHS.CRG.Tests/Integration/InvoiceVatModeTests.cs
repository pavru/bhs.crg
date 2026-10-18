using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Счёт с НДС сверху через адрес строк (issue #1497): режим едет со строками, сумма строки в ответе — с
/// НДС, бумажная — своим полем, и круг «прочитал → отправил» ничего не меняет.
/// </summary>
// ⚠️ Собрание указывается КАЖДОМУ классу: атрибут не наследуется от базового.
[Collection("Integration")]
public class InvoiceVatModeTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    private readonly InvoiceLineHost _host = host;

    /// <summary>Строка бумаги «сверху»: сумма без НДС — полем netAmount, amount не присылается.</summary>
    private static Dictionary<string, object?> Net(
        Guid? nomenclature, decimal quantity, decimal price, decimal? rate, decimal? net = null, Guid? id = null)
    {
        var line = Line(nomenclature, quantity, price, rate, id: id);
        line.Remove("amount");
        line["netAmount"] = net;
        return line;
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid invoice, string? mode, object[] lines) =>
        client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines",
            mode is null ? new Dictionary<string, object?> { ["lines"] = lines }
                : new Dictionary<string, object?> { ["vatMode"] = mode, ["lines"] = lines });

    private static async Task<JsonElement> OnTopAsync(HttpClient client, Guid invoice, params object[] lines)
    {
        var response = await PutAsync(client, invoice, "OnTop", lines);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static decimal? Money(JsonElement line, string key) =>
        line.GetProperty(key).ValueKind == JsonValueKind.Null ? null : line.GetProperty(key).GetDecimal();

    [Fact]
    public async Task Режим_едет_со_строками_и_сумма_в_ответе_с_НДС()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var records = await RecordsAsync(invoice);

        var view = await OnTopAsync(client, invoice, Net(cable, 10, 100, 22m), Net(conduit, 5, 100, 22m, net: 500m));

        Assert.Equal("OnTop", view.GetProperty("vatMode").GetString());
        var lines = view.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(1000m, Money(lines[0], "netAmount"));
        Assert.Equal(220m, Money(lines[0], "vatAmount"));
        Assert.Equal(1220m, Money(lines[0], "amount"));
        Assert.Equal(610m, Money(lines[1], "amount"));

        var totals = view.GetProperty("totals");
        Assert.Equal("OnTop", totals.GetProperty("mode").GetString());
        Assert.Equal(1500m, totals.GetProperty("net").GetDecimal());
        Assert.Equal(1830m, totals.GetProperty("amount").GetDecimal());
        Assert.Equal(330m, totals.GetProperty("vat").GetDecimal());
        Assert.Equal("lines", totals.GetProperty("vatFrom").GetString());

        // Смена режима — правка строк: одна запись журнала, со сменой режима словами.
        Assert.Equal(records + 1, await RecordsAsync(invoice));
    }

    /// <summary>
    /// Сторож С4: форма, отправившая прочитанное без правок, — бумажным полем своего режима, — не меняет
    /// ничего: ни строк, ни журнала, ни версии счёта. Иначе НДС прибавлялся бы на каждом сохранении.
    /// </summary>
    [Theory]
    [InlineData("Included")]
    [InlineData("OnTop")]
    public async Task Круг_прочитал_отправил_ничего_не_меняет(string mode)
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var first = mode == "OnTop"
            ? await OnTopAsync(client, invoice, Net(cable, 10, 100, 22m), Net(conduit, 3, 33.33m, 22m))
            : await LinesAsync(client, invoice, [Line(cable, 10, 122, 22m), Line(conduit, 3, 33.33m, 22m)]);
        var records = await RecordsAsync(invoice);
        var version = first.GetProperty("version").GetString();

        var paper = mode == "OnTop" ? "netAmount" : "amount";
        object[] again = [.. first.GetProperty("lines").EnumerateArray().Select(l => new Dictionary<string, object?>
        {
            ["id"] = l.GetProperty("id").GetString(),
            ["nomenclature"] = Reference(l.GetProperty("nomenclatureId").GetGuid()),
            ["quantity"] = Money(l, "quantity"),
            ["price"] = Money(l, "price"),
            ["vatRate"] = Money(l, "vatRate"),
            ["vatAmount"] = Money(l, "vatAmount"),
            [paper] = Money(l, paper),
        })];

        var response = await PutAsync(client, invoice, mode, again);
        await OkAsync(response);
        var second = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(version, second.GetProperty("version").GetString());
        Assert.Equal(records, await RecordsAsync(invoice));
        Assert.Equal(first.GetProperty("lines").GetRawText(), second.GetProperty("lines").GetRawText());
    }

    /// <summary>Сторож С5: сумма строки не полем своего режима — 400 с полем, которое ждут.</summary>
    [Fact]
    public async Task Чужое_поле_суммы_отказ()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        await OnTopAsync(client, invoice, Net(cable, 10, 100, 22m));

        // Прочитанный amount (с НДС) обратно как бумага — то самое удвоение.
        var doubled = await PutAsync(client, invoice, null, [Line(cable, 10, 100, 22m, amount: 1220m)]);
        Assert.Equal(HttpStatusCode.BadRequest, doubled.StatusCode);
        Assert.Contains("«netAmount»", await doubled.Content.ReadAsStringAsync());

        var net = await PutAsync(client, invoice, "Included", [Net(cable, 10, 100, 22m, net: 1000m)]);
        Assert.Equal(HttpStatusCode.BadRequest, net.StatusCode);
        Assert.Contains("«amount»", await net.Content.ReadAsStringAsync());

        // Отказ ничего не записал: режим и строка прежние.
        var read = await ReadAsync(client, invoice);
        Assert.Equal("OnTop", read.GetProperty("vatMode").GetString());
        Assert.Equal(1220m, Money(read.GetProperty("lines")[0], "amount"));
    }

    /// <summary>Смена режима обратно — строки досчитываются по новому правилу в той же правке.</summary>
    [Fact]
    public async Task Обратно_в_том_числе_бумажной_суммы_без_НДС_нет()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var onTop = await OnTopAsync(client, invoice, Net(cable, 10, 100, 22m));

        var response = await PutAsync(client, invoice, "Included",
            [Line(cable, 10, 122, 22m, id: LineId(onTop, 1))]);
        await OkAsync(response);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Included", view.GetProperty("vatMode").GetString());
        var line = view.GetProperty("lines")[0];
        Assert.Null(Money(line, "netAmount"));
        Assert.Equal(1220m, Money(line, "amount"));
        Assert.Equal(220m, Money(line, "vatAmount"));
        Assert.Null(view.GetProperty("totals").GetProperty("net").GetString());
    }

    /// <summary>
    /// Строка сверху без ставки: суммы с НДС у неё нет, и готовность называет причину — до расхождения
    /// суммы, которое из неё следует.
    /// </summary>
    [Fact]
    public async Task Строка_сверху_без_ставки_пункт_готовности()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        var view = await OnTopAsync(client, invoice, Net(cable, 10, 100, 22m), Net(conduit, 5, 100, null));

        Assert.Null(Money(view.GetProperty("lines")[1], "amount"));
        Assert.Equal(500m, Money(view.GetProperty("lines")[1], "netAmount"));

        var gaps = view.GetProperty("readiness").GetProperty("gaps").EnumerateArray().ToList();
        var rate = Assert.Single(gaps, g => g.GetProperty("kind").GetString() == "rate");
        Assert.Equal([2], rate.GetProperty("lines").EnumerateArray().Select(n => n.GetInt32()));
        Assert.True(gaps.FindIndex(g => g.GetProperty("kind").GetString() == "rate")
                    < gaps.FindIndex(g => g.GetProperty("kind").GetString() is "allocation" or "total"));

        var parsed = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        Assert.Equal(HttpStatusCode.BadRequest, parsed.StatusCode);
        Assert.Contains("нет ставки НДС", await parsed.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Бумага считает НДС от итога, строки — построчно: расхождение сверх допуска объясняется словами
    /// «НДС по строкам X, в бумаге Y», а не советом искать пропавшую строку.
    /// </summary>
    [Fact]
    public async Task Расхождение_из_за_НДС_от_итога_названо()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        // Строки: 1000 + 500 при 22 % — НДС по строкам 330. Бумага назвала НДС 335 и к оплате 1835:
        // расхождение 5,00 сверх допуска — ровно разница НДС.
        await OnTopAsync(client, invoice, Net(cable, 1, 1000, 22m), Net(conduit, 1, 500, 22m));
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, "ВТомЧислеНДС", 335m) }));
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(client, invoice, "Итого", 1835m) }));

        var read = await ReadAsync(client, invoice);
        var total = Assert.Single(read.GetProperty("readiness").GetProperty("gaps").EnumerateArray(),
            g => g.GetProperty("kind").GetString() == "total");
        Assert.Contains("5.00", total.GetProperty("label").GetString()!.Replace(',', '.'));

        var parsed = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        var text = await parsed.Content.ReadAsStringAsync();
        Assert.Contains("НДС по строкам 330", text.Replace(',', '.'));
        Assert.Contains("в бумаге 335", text.Replace(',', '.'));
        Assert.DoesNotContain("строка, которой нет в таблице", text);
    }

    /// <summary>
    /// Сторож С3: сумма с НДС у счёта сверху — производное двух чисел, и держит его база, а не только
    /// путь записи: мимо него (голым SQL) расходящееся число не ложится.
    /// </summary>
    [Fact]
    public async Task База_держит_сумму_с_НДС_как_сложение()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client);
        var view = await OnTopAsync(client, invoice, Net(cable, 10, 100, 22m));
        var line = LineId(view, 1);

        await using var db = new NpgsqlConnection(
            _host.Services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres"));
        await db.OpenAsync();

        foreach (var broken in new[]
                 {
                     "amount = 1000",                    // сумма с НДС, равная бумажной
                     "vat_amount = NULL",                // НДС пропал, сумма с НДС осталась
                 })
        {
            await using var command = new NpgsqlCommand(
                $"UPDATE {CostsDbContext.SchemaName}.invoice_lines SET {broken} WHERE id = @id", db);
            command.Parameters.AddWithValue("id", line);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal("ck_invoice_lines_net_amount", error.ConstraintName);
        }
    }
}
