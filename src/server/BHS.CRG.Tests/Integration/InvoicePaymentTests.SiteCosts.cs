using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// «Затраты по стройке» против реестра (задача G5, issue #1098, ТЗ COST-20). Тот же класс, что
/// <c>InvoicePaymentTests.cs</c>, — отдельным файлом по занятию.
/// </summary>
public partial class InvoicePaymentTests
{
    /// <summary>
    /// Приёмочный сторож этапа: <b>число отчёта равно итогу «Суммы» реестра под тем же отбором</b> —
    /// по стройке и по всем стройкам. Отбор здесь тот, что соберёт ссылка отчёта: объект, учётные
    /// месяцы, «не отклонён». Стенд общий, поэтому «все стройки» сверяются не с числом из головы, а с
    /// реестром: рядом лежат счета других тестов, и они обязаны войти в обе цифры одинаково.
    /// </summary>
    [Fact]
    public async Task Затраты_по_стройке_равны_итогу_реестра_под_тем_же_отбором()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, a, b) = await TwoSitesAsync(admin);

        var current = new DateOnly(today.Year, today.Month, 1);
        var paidOn = current.AddDays(-1);
        string was = $"{paidOn:MM.yyyy}", now = $"{current:MM.yyyy}";
        string wasKey = $"{paidOn:yyyy-MM}", nowKey = $"{current:yyyy-MM}";

        async Task<JsonElement> CostsAsync(string query) =>
            await admin.GetFromJsonAsync<JsonElement>($"/api/costs/site-costs?{query}");
        async Task<decimal?> RegistryAsync(params object[] conditions)
        {
            var filter = JsonSerializer.Serialize(new
            {
                type = "group", logic = "and",
                children = conditions.Append(new { type = "condition", column = "Состояние", op = "neq", value = "Отклонён" }),
            });
            var total = (await admin.GetFromJsonAsync<JsonElement>(
                    $"/api/tables/costs.invoices?columns=Номер,СуммаПоОтбору&totals=СуммаПоОтбору&limit=1&filter={Uri.EscapeDataString(filter)}"))
                .GetProperty("totals").GetProperty("СуммаПоОтбору").GetProperty("sum");
            return total.ValueKind == JsonValueKind.Null ? null : total.GetDecimal();
        }
        static object Period(params string[] months) => new { type = "condition", column = "УчётныйПериод", op = "in", values = months };
        static object On(string site) => new { type = "condition", column = "ОбъектыРазноски", op = "eq", value = site };
        static decimal Amount(JsonElement figure) => figure.GetProperty("amount").GetDecimal();

        // До оплаты: в затратах стройки ничего, а «к оплате» — её доля; и реестр под «не оплачен» говорит то же.
        var before = await CostsAsync($"site={a}&from={nowKey}");
        var nameA = before.GetProperty("site").GetProperty("name").GetString()!;
        Assert.Equal(0m, Amount(before.GetProperty("total")));
        Assert.Equal(40_000m, Amount(before.GetProperty("payable")));
        Assert.Equal(40_000m, await RegistryAsync(On(nameA),
            new { type = "condition", column = "СостояниеОплаты", op = "eq", value = "Не оплачен" }));

        // Стройка А закрыта по конец прошлого месяца — её 40 000 входят в затраты этого, остальное — прошлого.
        await CloseAsync(a, paidOn);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);

        var site = await CostsAsync($"site={a}&from={nowKey}&to={nowKey}");
        Assert.Equal(40_000m, Amount(site.GetProperty("total")));
        Assert.Equal(1, site.GetProperty("total").GetProperty("invoices").GetInt32());
        Assert.Equal([now], site.GetProperty("months").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(40_000m, Amount(Assert.Single(site.GetProperty("suppliers").EnumerateArray())));
        Assert.Equal(0m, Amount(site.GetProperty("payable")));
        Assert.Equal(Amount(site.GetProperty("total")), await RegistryAsync(On(nameA), Period(now)));
        // В прошлом месяце у стройки А затрат нет — как и в реестре под тем же отбором.
        Assert.Equal(0m, Amount((await CostsAsync($"site={a}&from={wasKey}&to={wasKey}")).GetProperty("total")));
        Assert.Null(await RegistryAsync(On(nameA), Period(was)));

        // Все стройки, два месяца: наши стройки — своими долями, а итог равен реестру под отбором по
        // учётному периоду, сколько бы чужих счетов ни лежало рядом. Строки складываются в итог:
        // стройки, вне строек и неразнесённое (его может не быть) — и больше ничего.
        var all = await CostsAsync($"from={wasKey}&to={nowKey}");
        decimal Site(Guid id) => Amount(all.GetProperty("sites").EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == id));
        Assert.Equal(40_000m, Site(a));
        Assert.Equal(60_000m, Site(b));
        var loose = all.GetProperty("unallocated");
        Assert.Equal(Amount(all.GetProperty("total")), await RegistryAsync(Period(was, now)));
        Assert.Equal(Amount(all.GetProperty("total")),
            all.GetProperty("sites").EnumerateArray().Sum(Amount) + all.GetProperty("articles").EnumerateArray().Sum(Amount)
            + (loose.ValueKind == JsonValueKind.Null ? 0m : Amount(loose)));

        // «Без НДС» не больше, чем с НДС, и говорит о себе полем ответа.
        var bare = await CostsAsync($"site={a}&from={nowKey}&vat=without");
        Assert.False(bare.GetProperty("withVat").GetBoolean());
        Assert.InRange(Amount(bare.GetProperty("total")), 0m, 40_000m);
    }

    /// <summary>
    /// Отказы отчёта — отказы, а не пустой отчёт: без права на отчёты — 403 (чтение счетов его не
    /// даёт), негодный месяц и период наоборот — 400 со словами, чужая стройка — 404.
    /// </summary>
    [Fact]
    public async Task Затраты_по_стройке_отказывают_словами_а_не_пустым_отчётом()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (supplier, _) = await SignInAsync("Supplier");

        Assert.Equal(HttpStatusCode.Forbidden, (await supplier.GetAsync("/api/costs/site-costs")).StatusCode);

        var month = await admin.GetAsync("/api/costs/site-costs?from=сентябрь");
        Assert.Equal(HttpStatusCode.BadRequest, month.StatusCode);
        Assert.Contains("2026-09", await ErrorAsync(month));

        var reversed = await admin.GetAsync("/api/costs/site-costs?from=2026-10&to=2026-09");
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        Assert.Contains("Период задан наоборот", await ErrorAsync(reversed));

        // Месяц вне календаря реестра — отказ: реестр такой не называет, и ссылка отчёта не нашла бы
        // ничего. Край календаря дат — тоже отказ словами, а не исключение (ревью PR #1200).
        foreach (var outside in new[] { "from=2019-06&to=2019-06", "to=9999-12" })
        {
            var beyond = await admin.GetAsync($"/api/costs/site-costs?{outside}");
            Assert.Equal(HttpStatusCode.BadRequest, beyond.StatusCode);
            Assert.Contains("в календаре нет", await ErrorAsync(beyond));
        }

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/costs/site-costs?site={Guid.NewGuid()}")).StatusCode);

        // Без параметров — текущий месяц компании и все стройки.
        var plain = await admin.GetFromJsonAsync<JsonElement>("/api/costs/site-costs");
        var today = await TodayAsync();
        Assert.Equal($"{today:yyyy-MM}", plain.GetProperty("from").GetString());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("site").ValueKind);
    }
}
