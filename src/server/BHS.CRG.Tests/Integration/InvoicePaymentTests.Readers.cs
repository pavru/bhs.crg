using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Tables;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Читатели книги исправлений (E2, issue #1100, PR 4, spec v3.3 п. 1.7–1.8): диалог закрытия и «Затраты
/// по стройке» видят счёт с книгой её строками — на живой базе, через адреса.
///
/// <para>Счёт — <c>LockedAsync</c>: 40 000 на стройку А и 60 000 на стройку Б, оплата 15 дней назад, А
/// закрыта по «10 дней назад». Затем отмена оплаты и новая оплата тем же днём (S21 (д), (е)): книга —
/// открытие (А, −15, +40 000), (Б, −15, +60 000); отмена (А, сегодня, −40 000), (Б, −15, −60 000); оплата
/// (А, −9, +40 000), (Б, −15, +60 000). В документе — доля А «9 дней назад», доля Б — в день оплаты.</para>
/// </summary>
public partial class InvoicePaymentTests
{
    /// <summary>
    /// I6: закрытие записывает взгляд «на момент закрытия». Счёт с книгой идёт строкой «Счета с книгой
    /// исправлений» — её числами, а не числами документа; в «вошедшие» он не входит, и ссылки в реестр у
    /// «вошедших» тогда нет.
    /// </summary>
    [Fact]
    public async Task Диалог_закрытия_считает_счёт_с_книгой_её_строками()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (_, a, b) = await BookedAsync(admin, today);

        // А закрывается дальше, по вчера: и документ, и книга кладут в эти дни 40 000 «9 дней назад», но
        // счёт с книгой идёт её строкой, а не «вошедшими». Поправка отмены (−40 000) лежит сегодня и в
        // это закрытие не входит.
        var preview = await ClosingPreviewAsync(admin, today.AddDays(-9), today.AddDays(-1), a);
        Assert.Equal((0, 0), Line(preview, "frozen", "entering"));
        Assert.Equal((1, 40_000m), Line(preview, "frozen", "corrected"));
        // Запирается тот же счёт, что назван строкой книги, — отдельной строки «запрутся» нет.
        Assert.Equal((0, 0), Line(preview, "frozen", "locked"));
        var corrected = Assert.Single(Assert.Single(preview.GetProperty("sections").EnumerateArray())
            .GetProperty("frozen").EnumerateArray());
        Assert.Equal("Счета с книгой исправлений", corrected.GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, corrected.GetProperty("link").ValueKind);

        // Б закрывается впервые: книга на день оплаты сложилась в 60 000 — столько же, сколько в документе.
        preview = await ClosingPreviewAsync(admin, today.AddDays(-40), today.AddDays(-1), b);
        Assert.Equal((1, 60_000m), Line(preview, "frozen", "corrected"));
        Assert.Equal((0, 0), Line(preview, "frozen", "entering"));

        // Повторное закрытие А после отмены (spec п. 1.6): в днях первого закрытия документ денег А больше
        // не держит, а книга держит прежние 40 000 — их и запишет закрытие. По документу здесь было бы пусто.
        await ReopenAsync(a, today.AddDays(-10));
        preview = await ClosingPreviewAsync(admin, today.AddDays(-40), today.AddDays(-10), a);
        Assert.Equal((1, 40_000m), Line(preview, "frozen", "corrected"));
        Assert.Equal((0, 0), Line(preview, "frozen", "entering"));
        Assert.Equal((0, 0), Line(preview, "frozen", "locked"));
    }

    /// <summary>
    /// «Без позиции номенклатуры сейчас» во взгляде закрытия — число ПЕРВОГО взгляда, как его найдёт реестр
    /// (PR 6c; ревью PR #1392): клиент ставит его указателем вместо «из них». В первом взгляде поля нет.
    /// </summary>
    [Fact]
    public async Task Без_позиции_сейчас_во_взгляде_закрытия_число_первого_взгляда()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var invoice = await CreateAsync(admin, complete: true);
        var view = await LinesAsync(admin, invoice, [Line(null, 10, 100)]);
        var (site, _) = await SiteAsync("Без позиции сейчас");
        await AllocateAsync(admin, invoice, LineId(view, 1), [Part(site, quantity: 10)]);
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "Итого", 1_000m) }));
        await PayAsync(admin, invoice, today, await PreviewAsync(admin, invoice, today), null);
        var range = $"site={site}&from={today:yyyy-MM}&to={today:yyyy-MM}";

        var corrected = await admin.GetFromJsonAsync<JsonElement>($"/api/costs/site-costs?{range}");
        var closing = await admin.GetFromJsonAsync<JsonElement>($"/api/costs/site-costs?{range}&view=closing");

        Assert.Equal((1, 1_000m), (corrected.GetProperty("unmatched").GetProperty("invoices").GetInt32(),
            corrected.GetProperty("unmatched").GetProperty("amount").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, corrected.GetProperty("unmatchedNow").ValueKind);
        Assert.Equal(corrected.GetProperty("unmatched").GetRawText(), closing.GetProperty("unmatchedNow").GetRawText());
    }

    /// <summary>
    /// «Затраты по стройке» — два взгляда (spec п. 1.7): по умолчанию «с исправлениями», как реестр;
    /// <c>view=closing</c> — по книге. Полоса знает, чем они различаются; ссылка у строки «на момент
    /// закрытия» — только под числом, которое покажет реестр.
    /// </summary>
    [Fact]
    public async Task Затраты_по_стройке_показывают_оба_взгляда()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var paidOn = today.AddDays(-15);
        var (invoice, a, _) = await LockedAsync(admin, today);
        var range = $"site={a}&from={paidOn:yyyy-MM}&to={today:yyyy-MM}";
        async Task<JsonElement> CostsAsync(string query) =>
            await admin.GetFromJsonAsync<JsonElement>($"/api/costs/site-costs?{range}{query}");
        static decimal Amount(JsonElement figure) => figure.GetProperty("amount").GetDecimal();

        // Без книги взгляды равны: полосы нет, ссылки на месте.
        var plain = await CostsAsync("&view=closing");
        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/costs/invoices"))
            .EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invoice).GetProperty("corrected").GetBoolean());
        Assert.Equal("closing", plain.GetProperty("view").GetString());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("differs").ValueKind);
        Assert.Equal(40_000m, Amount(plain.GetProperty("total")));
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("other").ValueKind);

        // После отмены оплаты счёта в документах нет, а книга держит его 40 000 днём оплаты и −40 000
        // сегодня: итоги равны нулю, различаются дни. Счёт, чьи деньги сложились в ноль, не считается и во
        // взгляде книги (решение владельца 16.10.2026, PR 6c): «один счёт на ноль» читался бы «счёт здесь есть».
        // Итог тогда тот же, что у реестра, — и ссылка на него честная.
        await OkAsync(await admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "ошиблись" }));
        var corrected = await CostsAsync("");
        Assert.Equal("corrected", corrected.GetProperty("view").GetString());
        Assert.Equal("details", corrected.GetProperty("differs").GetString());
        Assert.Equal(0, corrected.GetProperty("total").GetProperty("invoices").GetInt32());
        Assert.True(corrected.GetProperty("totalLinked").GetBoolean());
        var closing = await CostsAsync("&view=closing");
        Assert.Equal((0, 0m), (closing.GetProperty("total").GetProperty("invoices").GetInt32(), Amount(closing.GetProperty("total"))));
        Assert.True(closing.GetProperty("totalLinked").GetBoolean());
        Assert.Empty(closing.GetProperty("suppliers").EnumerateArray());
        // Полоса называет итог другого взгляда (PR 6b-3): у каждого — итог соседа.
        Assert.Equal(0m, Amount(corrected.GetProperty("other")));
        Assert.Equal(0m, Amount(closing.GetProperty("other")));
        // «Без позиции сейчас» — только во взгляде закрытия (PR 6c): в первом под итогом стоит «из них».
        Assert.Equal(JsonValueKind.Null, corrected.GetProperty("unmatchedNow").ValueKind);
        // Корректировки закрытых периодов (PR 6d): отмена оплаты легла днём записи, а не днём оплаты — это
        // −40 000 корректировки под итогом, хотя строки поставщика нет (деньги счёта в ней ноль). В первом взгляде
        // корректировок нет по устройству.
        var adjusted = closing.GetProperty("total").GetProperty("corrections");
        Assert.Equal((1, -40_000m, false),
            (adjusted.GetProperty("invoices").GetInt32(), Amount(adjusted), adjusted.GetProperty("whole").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, corrected.GetProperty("total").GetProperty("corrections").ValueKind);
        // Итог соседа — числом, без его корректировок: полоса называет только его.
        Assert.Equal(JsonValueKind.Null, corrected.GetProperty("other").GetProperty("corrections").ValueKind);
        // Список «Счета» помечает счёт с книгой исправлений (PR 6c).
        var listed = (await admin.GetFromJsonAsync<JsonElement>("/api/costs/invoices"))
            .EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == invoice);
        Assert.True(listed.GetProperty("corrected").GetBoolean());

        // Новая оплата: документ — 40 000 «9 дней назад», книга — те же 40 000 тремя строками. Итоги равны,
        // и строки «на момент закрытия» сошлись с реестром — ссылки у них есть.
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        corrected = await CostsAsync("");
        closing = await CostsAsync("&view=closing");
        Assert.Equal("details", closing.GetProperty("differs").GetString());
        Assert.Equal(40_000m, Amount(corrected.GetProperty("total")));
        Assert.Equal(40_000m, Amount(closing.GetProperty("total")));
        Assert.True(closing.GetProperty("totalLinked").GetBoolean());
        Assert.All(closing.GetProperty("suppliers").EnumerateArray(), s => Assert.True(s.GetProperty("linked").GetBoolean()));
        // Новая оплата — не корректировка: учётный день ей назначает правило оплаты, он открыт, и строка книги
        // легла своим днём. Корректировкой остаётся отмена оплаты: закрытый месяц держит 40 000, а разница
        // легла днём отмены.
        adjusted = closing.GetProperty("total").GetProperty("corrections");
        Assert.Equal((1, -40_000m), (adjusted.GetProperty("invoices").GetInt32(), Amount(adjusted)));
        // Разделы строк книги названы так же, как разделы долей: одни и те же названия в обоих взглядах.
        Assert.Equal(
            corrected.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("registry").GetString()),
            closing.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("registry").GetString()));

        var bogus = await admin.GetAsync($"/api/costs/site-costs?{range}&view=closed");
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
        Assert.Contains("closing", await ErrorAsync(bogus));
    }

    /// <summary>
    /// «Запрутся целиком» у закрытия стройки сравнивает запираемые счета с денежными строками ЭТОЙ
    /// стройки (ревью PR #1372): счёт с долей на неё без денег и деньгами на другой стройке в «вошедшие»
    /// не входит — значит, назван строкой «запрутся».
    /// </summary>
    [Fact]
    public async Task Запрётся_и_счёт_чьи_деньги_на_другой_стройке()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, a) = await PaidAsync(admin, today.AddDays(-20));
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE costs.invoice_lines SET amount = NULL WHERE invoice_id = {invoice} AND ordinal = 1");

        var preview = await ClosingPreviewAsync(admin, today.AddDays(-40), today.AddDays(-1), a);
        Assert.Equal((0, 0), Line(preview, "frozen", "entering"));
        Assert.Equal(1, Line(preview, "frozen", "locked").Count);
    }

    /// <summary>
    /// Реестр помечает исправленный после закрытия счёт (PR 6b-3): колонка «Исправлен после закрытия» — день
    /// последней записи книги, по ней отбирают; итог «Суммы» под учётным периодом говорит, что в названных
    /// месяцах есть исправления, — и тогда, когда исправление увело счёт из отбора; боковая панель — когда,
    /// чем и почему исправлен.
    /// </summary>
    [Fact]
    public async Task Реестр_помечает_исправленный_после_закрытия_счёт()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var paidOn = today.AddDays(-15);
        var (invoice, _, _) = await LockedAsync(admin, today);
        const string Column = InvoiceTable.CorrectedKey;
        async Task<JsonElement> RowAsync() =>
            await admin.GetFromJsonAsync<JsonElement>($"/api/tables/costs.invoices?columns=Номер,{Column}&row={invoice}");
        var number = (await RowAsync()).GetProperty("rows")[0].GetProperty("Номер").GetString();
        async Task<JsonElement> ByPeriodAsync(params object[] more) =>
            await admin.GetFromJsonAsync<JsonElement>(
                $"/api/tables/costs.invoices?columns=Номер,СуммаПоОтбору&totals=СуммаПоОтбору&filter=" +
                Uri.EscapeDataString(JsonSerializer.Serialize(new
                {
                    type = "group", logic = "and",
                    children = more.Prepend(new { type = "condition", column = "УчётныйПериод", op = "eq", value = $"{paidOn:MM.yyyy}" })
                        .Prepend(new { type = "condition", column = "Номер", op = "eq", value = number }),
                })));
        static string? Note(JsonElement table) =>
            table.GetProperty("totals").GetProperty("СуммаПоОтбору").GetProperty("note").GetString();

        // Запертый, но не исправленный: клетка пуста. Об итоге здесь не спрашиваем: подпись — о месяцах по
        // всем счетам с книгой, а база прогонов общая, и чужое исправление в тех же месяцах её законно даст.
        var before = await RowAsync();
        Assert.Equal(JsonValueKind.Null, before.GetProperty("rows")[0].GetProperty(Column).ValueKind);

        // Отмена оплаты пишет книгу: день записи — сегодня, причина — в оговорке панели.
        await OkAsync(await admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "ошиблись" }));
        var unpaid = await RowAsync();
        Assert.Equal($"{today:yyyy-MM-dd}", unpaid.GetProperty("rows")[0].GetProperty(Column).GetString());
        var note = unpaid.GetProperty("breakdown").GetProperty("note").GetString();
        Assert.StartsWith($"исправлен после закрытия {today:dd.MM.yyyy} (отмена оплаты: «ошиблись») — здесь счёт как он есть сейчас", note);
        // Неоплаченного счёта в отборе по учётному периоду больше нет — а итог месяца от этого и разошёлся с
        // закрытием: подпись о месяцах, а не об отобранных счетах (ревью PR #1390).
        var left = await ByPeriodAsync();
        Assert.Equal(0, left.GetProperty("count").GetInt32());
        Assert.EndsWith(InvoiceTable.CorrectedNote, Note(left));

        // Новая оплата — счёт снова в затратах периода, и итог «Суммы» говорит, что он с исправлениями.
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        Assert.EndsWith(InvoiceTable.CorrectedNote, Note(await ByPeriodAsync()));
        // Последняя запись теперь — новая оплата, без причины: оговорка называет её видом, а не выдаёт день
        // оплаты за день исправления с прежней причиной.
        Assert.StartsWith($"исправлен после закрытия {today:dd.MM.yyyy} (новая оплата) —",
            (await RowAsync()).GetProperty("breakdown").GetProperty("note").GetString());
        // Отбор «не пусто» по колонке находит его, «пусто» — нет: колонка отбирается в базе.
        Assert.Equal(1, (await ByPeriodAsync(new { type = "condition", column = Column, op = "is_not_empty" })).GetProperty("count").GetInt32());
        Assert.Equal(0, (await ByPeriodAsync(new { type = "condition", column = Column, op = "is_empty" })).GetProperty("count").GetInt32());
    }

    /// <summary>Запертый счёт с книгой: отмена оплаты и новая оплата тем же днём (S21 (д), (е)).</summary>
    private async Task<(Guid Invoice, Guid A, Guid B)> BookedAsync(HttpClient admin, DateOnly today)
    {
        var (invoice, a, b) = await LockedAsync(admin, today);
        var paidOn = today.AddDays(-15);
        await OkAsync(await admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "ошиблись" }));
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        return (invoice, a, b);
    }
}
