using System.Net.Http.Json;
using System.Text.Json;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Срез затрат по разделам стройки против реестра (задача G5b, issue #1198, ТЗ COST-20). Тот же класс,
/// что <c>InvoicePaymentTests.cs</c>, — отдельным файлом по занятию.
/// </summary>
public partial class InvoicePaymentTests
{
    /// <summary>
    /// Сторож задачи: <b>срез по разделам — та же сумма, что срез по контрагентам и итог стройки, а число
    /// каждой его строки равно итогу «Суммы» реестра под отбором, который соберёт ссылка</b> — объект,
    /// раздел, учётный период.
    ///
    /// <para>Раздел «4 эт.» заведён у ДВУХ строек нарочно: отбор реестра идёт по названию, и раздел,
    /// названный без стройки, показал бы под ссылкой деньги обеих (решение владельца 05.10.2026).</para>
    /// </summary>
    [Fact]
    public async Task Затраты_по_разделам_сходятся_с_итогом_стройки_и_с_реестром_под_отбором_по_разделу()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        string now = $"{today:MM.yyyy}", nowKey = $"{today:yyyy-MM}";

        var (a, ofA) = await SiteAsync("Разделы А", "4 эт.", "ливнёвка");
        var (b, ofB) = await SiteAsync("Разделы Б", "4 эт.");

        // 100 000: строка 40 000 — на «4 эт.» стройки А (20 000), на стройку А целиком (12 000) и на
        // «4 эт.» стройки Б (8 000); строка 60 000 — на «ливнёвку» стройки А.
        var invoice = await CreateAsync(admin, complete: true);
        var view = await LinesAsync(admin, invoice, [Line(cable, 100, 400), Line(conduit, 50, 1200)]);
        await AllocateAsync(admin, invoice, LineId(view, 1),
            [Part(a, quantity: 50, section: ofA[0]), Part(a, quantity: 30), Part(b, quantity: 20, section: ofB[0])]);
        await AllocateAsync(admin, invoice, LineId(view, 2), [Part(a, quantity: 50, section: ofA[1])]);
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "Итого", 100_000m) }));
        await PayAsync(admin, invoice, today, await PreviewAsync(admin, invoice, today), null);

        Task<JsonElement> TableAsync(string columns, params object[] conditions) => SectionTableAsync(admin, columns, conditions);
        Task<decimal?> RegistryAsync(params object[] conditions) => SectionRegistryAsync(admin, conditions);

        var report = await admin.GetFromJsonAsync<JsonElement>($"/api/costs/site-costs?site={a}&from={nowKey}&to={nowKey}");
        var nameA = report.GetProperty("site").GetProperty("name").GetString()!;
        var sections = report.GetProperty("sections").EnumerateArray().ToList();

        // Строки: разделы по названию, «без раздела» — последней. Названы коротко — стройка в заголовке.
        Assert.Equal(["4 эт.", "ливнёвка", "без раздела"], sections.Select(s => s.GetProperty("name").GetString()));
        Assert.Equal([20_000m, 60_000m, 12_000m], sections.Select(Amount));

        // Два среза — одна сумма: по разделам, по контрагентам и итог стройки.
        Assert.Equal(92_000m, Amount(report.GetProperty("total")));
        Assert.Equal(Amount(report.GetProperty("total")), sections.Sum(Amount));
        Assert.Equal(sections.Sum(Amount), report.GetProperty("suppliers").EnumerateArray().Sum(Amount));

        // Каждая строка ведёт в реестр, и итог «Суммы» там — её число, а не счёт целиком и не стройка.
        foreach (var section in sections)
        {
            var registry = section.GetProperty("registry").GetString()!;
            Assert.StartsWith($"{nameA} / ", registry);
            Assert.Equal(Amount(section), await RegistryAsync(On(nameA), Section("eq", registry), Period(now)));
        }

        // Одноимённый раздел другой стройки под ссылку не попадает, а «содержит» находит оба — так и задумано.
        Assert.Equal(20_000m, await RegistryAsync(Section("eq", $"{nameA} / 4 эт.")));
        Assert.Equal(28_000m, await RegistryAsync(Section("contains", "4 эт."), Period(now)));
        // Раздел и объект спрашивают у ОДНОЙ доли: «4 эт.» стройки А на стройке Б не лежит.
        var nameB = (await admin.GetFromJsonAsync<JsonElement>($"/api/costs/site-costs?site={b}&from={nowKey}&to={nowKey}"))
            .GetProperty("site").GetProperty("name").GetString()!;
        Assert.Null(await RegistryAsync(On(nameB), Section("eq", $"{nameA} / 4 эт.")));

        // Клетка «Раздел» — перечень разделов счёта вместе со стройкой; подпись «Суммы» называет раздел.
        var table = await TableAsync("Номер,РазделыРазноски,СуммаПоОтбору", Section("eq", $"{nameA} / без раздела"));
        var row = Assert.Single(table.GetProperty("rows").EnumerateArray());
        Assert.Equal(
            new[] { $"{nameA} / 4 эт.", $"{nameA} / без раздела", $"{nameA} / ливнёвка", $"{nameB} / 4 эт." }.Order(StringComparer.Ordinal),
            row.GetProperty("РазделыРазноски").EnumerateArray().Select(v => v.GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal(12_000m, row.GetProperty("СуммаПоОтбору").GetDecimal());
        Assert.Contains($"раздел: {nameA} / без раздела",
            table.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "СуммаПоОтбору")
                .GetProperty("note").GetString());

        // Боковая панель строки называет раздел доли — коротко, стройка стоит рядом; помечена названная.
        var opened = (await admin.GetFromJsonAsync<JsonElement>($"/api/tables/costs.invoices?row={invoice}&filter=" +
            Uri.EscapeDataString(JsonSerializer.Serialize(Section("eq", $"{nameA} / ливнёвка"))))).GetProperty("breakdown");
        var parts = opened.GetProperty("rows").EnumerateArray().Select(r => (
            Object: r.GetProperty("values").GetProperty("Объект").GetString(),
            Section: r.GetProperty("values").GetProperty("Раздел").GetString(),
            Share: r.GetProperty("values").GetProperty("Доля").GetDecimal(),
            Named: r.GetProperty("named").GetBoolean())).ToList();
        Assert.Equal(
            [(nameA, "4 эт.", 20_000m, false), (nameA, "без раздела", 12_000m, false), (nameA, "ливнёвка", 60_000m, true), (nameB, "4 эт.", 8_000m, false)],
            parts.Select(p => (p.Object!, p.Section!, p.Share, p.Named)));
    }

    /// <summary>
    /// <b>Строка среза — название, а не раздел</b> (ревью PR #1209). Ядро не запрещает двум разделам
    /// стройки зваться одинаково, и разделу — зваться «без раздела»; отбор реестра идёт по названию и
    /// складывает их. Сложи отчёт по идентификатору — две одноимённые строки вели бы в реестр с ОДНИМ итогом
    /// на обе, и ни одна с ним не сошлась бы. Молча: обе цифры правдоподобны.
    /// </summary>
    [Fact]
    public async Task Одноимённые_разделы_в_срезе_одной_строкой_и_она_сходится_с_реестром()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        string now = $"{today:MM.yyyy}", nowKey = $"{today:yyyy-MM}";
        var (site, of) = await SiteAsync("Разделы тёзки", "тёзка", "тёзка", "без раздела");

        // 40 000 одной строкой: 20 000 и 10 000 — на два раздела «тёзка», 6 000 — на стройку целиком,
        // 4 000 — на раздел, названный «без раздела».
        var invoice = await CreateAsync(admin, complete: true);
        var view = await LinesAsync(admin, invoice, [Line(cable, 100, 400)]);
        await AllocateAsync(admin, invoice, LineId(view, 1),
        [
            Part(site, quantity: 50, section: of[0]), Part(site, quantity: 25, section: of[1]),
            Part(site, quantity: 15), Part(site, quantity: 10, section: of[2]),
        ]);
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "Итого", 40_000m) }));
        await PayAsync(admin, invoice, today, await PreviewAsync(admin, invoice, today), null);

        var report = await admin.GetFromJsonAsync<JsonElement>($"/api/costs/site-costs?site={site}&from={nowKey}&to={nowKey}");
        var name = report.GetProperty("site").GetProperty("name").GetString()!;
        var sections = report.GetProperty("sections").EnumerateArray().ToList();

        Assert.Equal(
            [("тёзка", 30_000m), ("без раздела", 10_000m)],
            sections.Select(s => (s.GetProperty("name").GetString()!, Amount(s))));
        Assert.Equal(Amount(report.GetProperty("total")), sections.Sum(Amount));
        foreach (var section in sections)
            Assert.Equal(Amount(section), await SectionRegistryAsync(admin,
                On(name), Section("eq", section.GetProperty("registry").GetString()!), Period(now)));
    }

    private static async Task<JsonElement> SectionTableAsync(HttpClient client, string columns, params object[] conditions)
    {
        var filter = JsonSerializer.Serialize(new { type = "group", logic = "and", children = conditions });
        return await client.GetFromJsonAsync<JsonElement>(
            $"/api/tables/costs.invoices?columns={columns}&totals=СуммаПоОтбору&filter={Uri.EscapeDataString(filter)}");
    }

    /// <summary>Итог «Суммы» реестра под отбором ссылки отчёта: её условия и «не отклонён».</summary>
    private static async Task<decimal?> SectionRegistryAsync(HttpClient client, params object[] conditions)
    {
        var total = (await SectionTableAsync(client, "Номер,СуммаПоОтбору", [.. conditions,
                new { type = "condition", column = "Состояние", op = "neq", value = "Отклонён" }]))
            .GetProperty("totals").GetProperty("СуммаПоОтбору").GetProperty("sum");
        return total.ValueKind == JsonValueKind.Null ? null : total.GetDecimal();
    }

    private static object Period(string month) => new { type = "condition", column = "УчётныйПериод", op = "in", values = new[] { month } };
    private static object On(string site) => new { type = "condition", column = "ОбъектыРазноски", op = "eq", value = site };
    private static object Section(string op, string name) => new { type = "condition", column = "РазделыРазноски", op, value = name };
    private static decimal Amount(JsonElement figure) => figure.GetProperty("amount").GetDecimal();
}
