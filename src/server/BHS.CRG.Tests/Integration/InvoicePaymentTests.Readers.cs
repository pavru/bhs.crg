using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
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
        Assert.Equal("closing", plain.GetProperty("view").GetString());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("differs").ValueKind);
        Assert.Equal(40_000m, Amount(plain.GetProperty("total")));

        // После отмены оплаты счёта в документах нет, а книга держит его 40 000 днём оплаты и −40 000
        // сегодня: итоги равны нулю, различаются дни. Счёт в книге — «один счёт на ноль», и реестр,
        // где его нет, такой ссылки не подтвердит.
        await OkAsync(await admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "ошиблись" }));
        var corrected = await CostsAsync("");
        Assert.Equal("corrected", corrected.GetProperty("view").GetString());
        Assert.Equal("details", corrected.GetProperty("differs").GetString());
        Assert.Equal(0, corrected.GetProperty("total").GetProperty("invoices").GetInt32());
        Assert.True(corrected.GetProperty("totalLinked").GetBoolean());
        var closing = await CostsAsync("&view=closing");
        Assert.Equal((1, 0m), (closing.GetProperty("total").GetProperty("invoices").GetInt32(), Amount(closing.GetProperty("total"))));
        Assert.False(closing.GetProperty("totalLinked").GetBoolean());

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
