using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Быстрая разноска документа и матрица (задача F2, issue #1086, ТЗ COST-11, COST-12) — через адреса
/// модуля, на живой базе.
///
/// <para>Арифметика раскладки проверяется без базы (<c>AllocationSplitTests</c>); здесь — два сторожа
/// задачи: предпросмотр совпадает с записанным, и пересчёт счёта без строк по появившимся строкам не
/// теряет и не задваивает суммы.</para>
/// </summary>
// ⚠️ Собрание указывается КАЖДОМУ классу: атрибут не наследуется от базового.
[Collection("Integration")]
public class InvoiceAllocationMatrixTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    /// <summary>
    /// Сторож F2: показанное до применения — то же, что записано. Сравниваются числа частей в том виде,
    /// в каком их рисует форма, — идентификаторы частей предпросмотра временные и в сравнение не идут.
    /// </summary>
    [Fact]
    public async Task Предпросмотр_поровну_совпадает_с_записанным()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 48.33m), Line(conduit, quantity: 7.5m, price: 3m)]);
        var sites = await SitesAsync(3);

        var preview = await PreviewAsync(client, invoice, "equal", [.. sites.Select(s => Target(s))]);
        var view = await ApplyAsync(client, invoice, preview.GetProperty("apply"));

        foreach (var line in view.GetProperty("lines").EnumerateArray())
        {
            var id = line.GetProperty("id").GetString()!;
            Assert.Equal(Cells(preview.GetProperty("lines").GetProperty(id)), Cells(line.GetProperty("allocation")));
        }

        // 10 шт на три объекта — целыми: 3 + 3 + 4, и остаток помечен у последней.
        var first = preview.GetProperty("lines").GetProperty(LineId(view, 1).ToString()).GetProperty("parts");
        Assert.Equal([3m, 3m, 4m], first.EnumerateArray().Select(p => p.GetProperty("quantity").GetDecimal()));
        Assert.Contains(preview.GetProperty("remainders").EnumerateArray(), r =>
            r.GetProperty("line").GetGuid() == LineId(view, 1) && r.GetProperty("construction").GetGuid() == sites[2]);

        Assert.True(view.GetProperty("allocation").GetProperty("allocated").GetBoolean());
    }

    /// <summary>Признак из живого прогона: 100 ₽ на три объекта — ровно 100,00, копейка у последней.</summary>
    [Fact]
    public async Task Сто_рублей_на_три_объекта_копейка_уходит_в_последнюю()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [new Dictionary<string, object?>
        {
            ["nomenclature"] = Reference(conduit), ["supplierText"] = "Доставка", ["amount"] = 100m,
        }]);
        var sites = await SitesAsync(3);

        var preview = await PreviewAsync(client, invoice, "equal", [.. sites.Select(s => Target(s))]);
        var parts = preview.GetProperty("apply").GetProperty("lines")[0].GetProperty("parts");

        Assert.Equal([33.33m, 33.33m, 33.34m], parts.EnumerateArray().Select(p => p.GetProperty("amount").GetDecimal()));
        Assert.Equal(sites[2], preview.GetProperty("remainders")[0].GetProperty("construction").GetGuid());
    }

    [Fact]
    public async Task По_процентам_раскладывает_по_весам_и_требует_ровно_ста()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await LinesAsync(client, invoice, [Line(cable, quantity: 200, price: 1m)]);
        var sites = await SitesAsync(2);

        var preview = await PreviewAsync(client, invoice, "percent", [Target(sites[0], 30m), Target(sites[1], 70m)]);
        Assert.Equal([60m, 140m], preview.GetProperty("apply").GetProperty("lines")[0].GetProperty("parts")
            .EnumerateArray().Select(p => p.GetProperty("quantity").GetDecimal()));

        var wrong = await PreviewRawAsync(client, invoice, "percent", [Target(sites[0], 30m), Target(sites[1], 60m)]);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("ровно 100", await wrong.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Сторож F2: счёт без строк разнесён суммой, строки появились — пересчёт переносит разноску на строки
    /// в тех же пропорциях, и сумма всех частей равна сумме счёта: не потеряна и не задвоена.
    /// </summary>
    [Fact]
    public async Task Пересчёт_счёта_без_строк_не_теряет_и_не_задваивает_суммы()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new
        {
            requisites = await RequisitesWithAsync(client, invoice, "Итого", 1_000m),
        }));
        var sites = await SitesAsync(2);

        var byTotal = await PreviewAsync(client, invoice, "percent", [Target(sites[0], 25m), Target(sites[1], 75m)]);
        var view = await ApplyAsync(client, invoice, byTotal.GetProperty("apply"));
        var document = view.GetProperty("allocation").GetProperty("document");
        Assert.Equal([250m, 750m], document.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("amount").GetDecimal()));
        Assert.Equal(0m, document.GetProperty("unallocatedAmount").GetDecimal());

        // Строки появились — разноска суммой ждёт пересчёта, и «разобран» до него не проходит.
        view = await LinesAsync(client, invoice, [Line(cable, quantity: 40, price: 20m), Line(conduit, quantity: 8, price: 25m)]);
        Assert.True(view.GetProperty("allocation").GetProperty("document").GetProperty("pending").GetBoolean());
        var refused = await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("ждёт пересчёта", await refused.Content.ReadAsStringAsync());

        var recount = await PreviewAsync(client, invoice, "document", []);
        view = await ApplyAsync(client, invoice, recount.GetProperty("apply"));

        var allocation = view.GetProperty("allocation");
        Assert.Empty(allocation.GetProperty("document").GetProperty("parts").EnumerateArray());
        var parts = view.GetProperty("lines").EnumerateArray()
            .SelectMany(l => l.GetProperty("allocation").GetProperty("parts").EnumerateArray())
            .ToList();
        Assert.Equal(1_000m, parts.Sum(p => p.GetProperty("amount").GetDecimal()));
        Assert.Equal(250m, parts.Where(p => p.GetProperty("constructionId").GetGuid() == sites[0])
            .Sum(p => p.GetProperty("amount").GetDecimal()));
        Assert.True(allocation.GetProperty("allocated").GetBoolean());

        await OkAsync(await client.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));
    }

    /// <summary>
    /// Разноска суммой неполная (из 1 000 на объект — 300): пересчёт оставляет нерешённое нерешённым, а не
    /// раздаёт его выбранному объекту.
    /// </summary>
    [Fact]
    public async Task Пересчёт_неполной_разноски_суммой_оставляет_нерешённое()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await TotalAsync(client, 1_000m);
        var sites = await SitesAsync(1);

        await ApplyAsync(client, invoice, JsonSerializer.SerializeToElement(new
        {
            lines = Array.Empty<object>(), document = new[] { Part(sites[0], amount: 300m) },
        }));
        await LinesAsync(client, invoice, [Line(cable, quantity: 50, price: 20m)]);

        var recount = await PreviewAsync(client, invoice, "document", []);
        var line = recount.GetProperty("lines").EnumerateObject().Single().Value;

        Assert.Equal(15m, line.GetProperty("parts")[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(35m, line.GetProperty("unallocatedQuantity").GetDecimal());
    }

    /// <summary>Корректировочный счёт (к оплате −1 000): пересчёт берёт пропорцию по модулю, а не отказывает.</summary>
    [Fact]
    public async Task Пересчёт_счёта_с_отрицательной_суммой_к_оплате()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await TotalAsync(client, -1_000m);
        var sites = await SitesAsync(2);

        await ApplyAsync(client, invoice, JsonSerializer.SerializeToElement(new
        {
            lines = Array.Empty<object>(),
            document = new[] { Part(sites[0], amount: -400m), Part(sites[1], amount: -600m) },
        }));
        await LinesAsync(client, invoice, [new Dictionary<string, object?>
        {
            ["nomenclature"] = Reference(conduit), ["supplierText"] = "Скидка", ["amount"] = -1_000m,
        }]);

        var recount = await PreviewAsync(client, invoice, "document", []);
        Assert.Equal([-400m, -600m], recount.GetProperty("apply").GetProperty("lines")[0].GetProperty("parts")
            .EnumerateArray().Select(p => p.GetProperty("amount").GetDecimal()));
    }

    /// <summary>
    /// Матрица открыта, сосед тем временем удалил часть — запись набора по устаревшему виду отвергается, а не
    /// возвращает удалённое.
    /// </summary>
    [Fact]
    public async Task Набор_по_устаревшему_виду_отвергается()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 10, price: 1m)]);
        var sites = await SitesAsync(2);

        var preview = await PreviewAsync(client, invoice, "equal", [.. sites.Select(s => Target(s))]);
        view = await ApplyAsync(client, invoice, preview.GetProperty("apply"));
        var opened = view.GetProperty("allocation").GetProperty("stamp").GetString();

        await AllocateAsync(client, invoice, LineId(view, 1), [Part(sites[0], quantity: 5)]);

        var stale = await ApplyRawAsync(client, invoice, preview.GetProperty("apply"), opened);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("изменили, пока матрица была открыта", await stale.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Набор_без_одной_из_строк_отвергается()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 1m), Line(conduit, quantity: 1, price: 1m)]);

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/allocation", new
        {
            lines = new[] { new { line = LineId(view, 1), parts = Array.Empty<object>() } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Не присланы строки 2", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Часть_счёта_целиком_при_строках_отвергается()
    {
        var (client, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(client, complete: true);
        var view = await LinesAsync(client, invoice, [Line(cable, quantity: 1, price: 1m)]);
        var sites = await SitesAsync(1);

        var response = await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/allocation", new
        {
            lines = new[] { new { line = LineId(view, 1), parts = Array.Empty<object>() } },
            document = new[] { Part(sites[0], amount: 1m) },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("у счёта есть строки", await response.Content.ReadAsStringAsync());
    }

    /// <summary>Предпросмотр — только тому, кто может записать: кнопка без права не нужна вовсе.</summary>
    [Fact]
    public async Task Без_права_разноски_предпросмотра_нет()
    {
        var (admin, _) = await SignInAsync("Admin");
        var invoice = await CreateAsync(admin);
        var sites = await SitesAsync(1);

        var (user, _) = await SignInAsync("User");
        var response = await PreviewRawAsync(user, invoice, "equal", [Target(sites[0])]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<Guid> TotalAsync(HttpClient client, decimal total)
    {
        var invoice = await CreateAsync(client, complete: true);
        await OkAsync(await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new
        {
            requisites = await RequisitesWithAsync(client, invoice, "Итого", total),
        }));
        return invoice;
    }

    private async Task<Guid[]> SitesAsync(int count)
    {
        var sites = new Guid[count];
        for (var index = 0; index < count; index++)
            sites[index] = (await SiteAsync($"Объект {index + 1}")).Site;
        return sites;
    }

    private static Dictionary<string, object?> Target(Guid site, decimal? percent = null) =>
        new() { ["construction"] = site.ToString(), ["percent"] = percent };

    private static Task<HttpResponseMessage> PreviewRawAsync(
        HttpClient client, Guid invoice, string method, object[] targets) =>
        client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/allocation/preview", new { method, targets });

    private static async Task<JsonElement> PreviewAsync(HttpClient client, Guid invoice, string method, object[] targets)
    {
        var response = await PreviewRawAsync(client, invoice, method, targets);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Записать набор матрицы с отметкой версии, которую форма взяла бы из открытого счёта. Без
    /// <paramref name="stamp" /> — нынешняя отметка счёта.
    /// </summary>
    private static async Task<HttpResponseMessage> ApplyRawAsync(HttpClient client, Guid invoice, JsonElement state, string? stamp = null)
    {
        stamp ??= await StampAsync(client, invoice);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(state.GetRawText())!;
        body["stamp"] = JsonSerializer.SerializeToElement(stamp);
        return await client.PutAsJsonAsync($"/api/costs/invoices/{invoice}/allocation", body);
    }

    private static async Task<JsonElement> ApplyAsync(HttpClient client, Guid invoice, JsonElement state)
    {
        var response = await ApplyRawAsync(client, invoice, state);
        await OkAsync(response);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> StampAsync(HttpClient client, Guid invoice) =>
        (await ReadAsync(client, invoice)).GetProperty("allocation").GetProperty("stamp").GetString()!;

    /// <summary>
    /// Клетки разноски строки текстом — то, что рисует форма, без временных идентификаторов.
    ///
    /// <para>Числа — ЧИСЛАМИ, а не записью в JSON: записанное количество читается из колонки с тремя
    /// знаками («3.000»), посчитанное — без них («3»). Форма разбирает оба в одно число и рисует одинаково;
    /// посимвольно одинаковую отрисовку проверяет живой прогон.</para>
    /// </summary>
    private static string Cells(JsonElement allocation) =>
        string.Join(" | ", allocation.GetProperty("parts").EnumerateArray().Select(p =>
            $"{p.GetProperty("constructionId")}:{Number(p.GetProperty("quantity"))}:{Number(p.GetProperty("amount"))}:" +
            $"{Number(p.GetProperty("rounding"))}:{Number(p.GetProperty("discrepancy"))}"))
        + $" || {Number(allocation.GetProperty("unallocatedQuantity"))}:{Number(allocation.GetProperty("unallocatedAmount"))}";

    private static string Number(JsonElement value) => value.ValueKind == JsonValueKind.Null
        ? "—"
        : (value.GetDecimal() / 1.000000000000000000000000000000000m).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
