using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Оплата и её отмена у счёта с книгой или запертого (E2, issue #1100, PR 3, spec v3.3 п. 1.5): права,
/// строки книги на живой базе и выходы неоплаченного счёта с книгой. Сценарии S21 (б), (д), (е), S03, S12,
/// S15 и S20 пятого и четвёртого прогонов — на живой базе.
///
/// <para>Счёт везде — <c>LockedAsync</c>: строка 1 — 40 000 на стройку А, строка 2 — 60 000 на стройку Б,
/// оплата 15 дней назад, А закрыта по «10 дней назад». Числа книги — проводкой
/// (<c>InvoiceBookPostingTests</c>); здесь — что они доходят до базы через адреса оплаты.</para>
/// </summary>
public partial class InvoicePaymentTests
{
    /// <summary>
    /// S21 (б), (д), (е) и S20: отмена оплаты запертого — правом оплаты и исправления, заводит книгу;
    /// новая оплата неоплаченного с книгой — одним правом оплаты и продолжает её. Строки — по правилу
    /// (б′): закрытый день стройки А правится днём записи, открытые дни — своими.
    /// </summary>
    [Fact]
    public async Task Отмена_оплаты_запертого_и_новая_оплата_пишут_книгу()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (payer, _) = await SignInAsync(await RoleAsync("costs.invoice.read", "costs.invoice.pay"));
        var today = await TodayAsync();
        var (invoice, a, b) = await LockedAsync(admin, today);

        // S21 (б): право оплаты без права исправления — отказ, и счёт не тронут. Отказ о праве — раньше
        // версии формы, как у исправления: устаревшая форма не отправляет перечитывать ради 403.
        var stale = (ulong.Parse((await ReadAsync(admin, invoice)).GetProperty("version").GetString()!) - 1).ToString();
        foreach (var version in new[] { (string?)null, stale })
        {
            var refused = await UnpayRawAsync(payer, invoice, "ошиблись", version);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("Исправлять счета закрытого периода", await ErrorAsync(refused));
        }
        Assert.Equal((0, 0), await BookAsync(invoice));

        // S21 (д): с обоими правами — проходит. Открытие — деньги, как лежали, своими днями; отмена — доля
        // закрытой А уходит днём отмены, доля открытой Б — своим днём.
        await OkAsync(await admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "ошиблись" }));
        var paidOn = today.AddDays(-15);
        var book = await RecordsAsync(invoice);
        Assert.Equal([InvoiceBookKind.Opening, InvoiceBookKind.Unpayment], book.Select(r => r.Kind));
        Assert.All(book, r => Assert.Equal(today, r.Day));
        Assert.Equal([(a, paidOn, 40_000m), (b, paidOn, 60_000m)], Cells(book[0]));
        Assert.Equal([(a, today, -40_000m), (b, paidOn, -60_000m)], Cells(book[1]));
        // Причина — и в книге: она история закрытых месяцев, а не только журнал ядра.
        Assert.Equal([null, "ошиблись"], await ReasonsAsync(invoice));
        Assert.Empty(await DatesAsync(invoice));
        Assert.Equal((2, 0), await BookAsync(invoice));

        // Журнал — как у любой отмены: причина, ни одной суммы.
        using (var scope = host.Services.CreateScope())
        {
            var record = (await scope.ServiceProvider.GetRequiredService<IActivityLog>()
                    .ReadAsync(0, 50, ActivityVisibility.Whole, "costs.invoice.unpaid"))
                .Single(r => r.TargetId == invoice.ToString());
            Assert.Equal("не оплачен. Причина: ошиблись", record.After);
        }

        // Неоплаченный с книгой правится свободно (п. 1.5): денег нет ни до, ни после — в книгу ничего.
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "Назначение", "поправили") }));
        Assert.Equal(2, (await RecordsAsync(invoice)).Count);

        // S21 (е) и S20: новая оплата тем же задним числом — одним правом оплаты. Доля А идёт в первый
        // открытый день, и строка книги — тем же днём: пара пуста и в книге, и в документе.
        await PayAsync(payer, invoice, paidOn, await PreviewAsync(payer, invoice, paidOn), null);
        book = await RecordsAsync(invoice);
        Assert.Equal(InvoiceBookKind.Payment, book[2].Kind);
        Assert.Equal(today, book[2].Day);
        Assert.Equal([(a, today.AddDays(-9), 40_000m), (b, paidOn, 60_000m)], Cells(book[2]));
        Assert.Equal(new Dictionary<Guid, DateOnly> { [a] = today.AddDays(-9), [b] = paidOn }, await DatesAsync(invoice));

        // Оплаченный с книгой — снова «требует исправления»: обычная правка — отказ, отмена без права
        // исправления — отказ.
        var plain = await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "Назначение", "ещё раз") });
        Assert.Equal(HttpStatusCode.Conflict, plain.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await payer.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "снова" })).StatusCode);
        Assert.Equal(3, (await RecordsAsync(invoice)).Count);
    }

    /// <summary>
    /// S15: опечатку в платёжном документе запертого счёта правят без исправления — событием журнала; в
    /// книгу ничего.
    /// </summary>
    [Fact]
    public async Task Платёжный_документ_запертого_правится_без_книги()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, _, _) = await LockedAsync(admin, today);

        var described = await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}/paid", new { document = "п/п 125" });
        await OkAsync(described);
        Assert.Equal("п/п 125", (await ReadAsync(admin, invoice)).GetProperty("payment").GetProperty("document").GetString());
        Assert.Equal((0, 0), await BookAsync(invoice));

        // Деньги — по-прежнему только исправлением.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "Назначение", "поправили") })).StatusCode);
    }

    /// <summary>
    /// S12: неоплаченный счёт с книгой отклоняют и возвращают в черновик — а удалить не дают: выход у него
    /// «Отклонён». Незапертый счёт без книги отмену оплаты проводит как прежде — без книги.
    /// </summary>
    [Fact]
    public async Task Неоплаченный_с_книгой_не_удаляется_а_без_книги_книги_не_заводит()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, _, _) = await LockedAsync(admin, today);

        await OkAsync(await admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "не платим" }));
        await OkAsync(await admin.PostAsync($"/api/costs/invoices/{invoice}/rejected", null));
        await OkAsync(await admin.PostAsync($"/api/costs/invoices/{invoice}/draft", null));
        var deleted = await admin.DeleteAsync($"/api/costs/invoices/{invoice}");
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Contains("книга исправлений", await ErrorAsync(deleted));
        Assert.Equal(2, (await RecordsAsync(invoice)).Count);

        // Тот же путь у незапертого: книги нет и после.
        var (open, _, _) = await TwoSitesAsync(admin);
        await PayAsync(admin, open, today, await PreviewAsync(admin, open, today), null);
        await OkAsync(await admin.PostAsJsonAsync($"/api/costs/invoices/{open}/unpaid", new { reason = "не тот" }));
        Assert.Equal((0, 0), await BookAsync(open));
    }

    /// <summary>Отмена оплаты с названной версией формы; <c>null</c> — свежая (её подставит обработчик клиента).</summary>
    private static Task<HttpResponseMessage> UnpayRawAsync(HttpClient client, Guid invoice, string reason, string? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/costs/invoices/{invoice}/unpaid")
        {
            Content = JsonContent.Create(new { reason }),
        };
        if (version is not null) request.Headers.TryAddWithoutValidation(SeenInvoiceVersion.Header, $"\"{version}\"");
        return client.SendAsync(request);
    }

    /// <summary>Причины записей книги — по порядку.</summary>
    private async Task<List<string?>> ReasonsAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CostsDbContext>().InvoiceBookEntries.AsNoTracking()
            .Where(e => e.InvoiceId == invoice).OrderBy(e => e.Seq).Select(e => e.Reason).ToListAsync();
    }

    /// <summary>Книга счёта, как лежит в базе, — по порядку записей.</summary>
    private async Task<IReadOnlyList<BookRecord>> RecordsAsync(Guid invoice)
    {
        using var scope = host.Services.CreateScope();
        return [.. (await InvoiceBook.ReadAsync(scope.ServiceProvider.GetRequiredService<CostsDbContext>(), invoice,
            CancellationToken.None)).Select(e => e.Record)];
    }

    /// <summary>Строки записи книги: стройка, день, сумма с НДС — по стройке.</summary>
    private static List<(Guid Site, DateOnly Day, decimal Gross)> Cells(BookRecord record) =>
        [.. record.Lines.Select(l => (l.Cell.Target!.Value.ConstructionId!.Value, l.Day, l.Gross)).OrderBy(l => l.Gross < 0 ? -l.Gross : l.Gross)];
}
