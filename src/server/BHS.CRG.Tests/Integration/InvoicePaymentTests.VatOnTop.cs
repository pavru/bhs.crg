using System.Net.Http.Json;
using System.Text.Json;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Счёт с НДС сверху сквозь оплату и отчёт (issue #1497) — тот же класс, что <c>InvoicePaymentTests.cs</c>,
/// отдельным файлом по занятию.
/// </summary>
public partial class InvoicePaymentTests
{
    /// <summary>
    /// Сторож С6 — пример владельца (числа выдуманные): строки 1 000 + 500 без НДС, НДС 22 % сверху, к
    /// оплате 1 830. Расхождение 0, «Разобран» доступен, «Затраты по стройке» без НДС — 1 500, с НДС —
    /// 1 830. Без единой правки разноски, отчётов и книги: ни один читатель суммы строки режима не учил.
    /// </summary>
    [Fact]
    public async Task Счёт_с_НДС_сверху_сходится_и_ложится_в_затраты()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var invoice = await CreateAsync(admin, complete: true);

        var response = await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}/lines", new
        {
            vatMode = "OnTop",
            lines = new object[]
            {
                new { nomenclature = Reference(cable), quantity = 10m, price = 100m, vatRate = 22m },
                new { nomenclature = Reference(conduit), quantity = 5m, price = 100m, vatRate = 22m },
            },
        });
        await OkAsync(response);
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();

        var (site, _) = await SiteAsync("НДС сверху");
        await AllocateAsync(admin, invoice, LineId(view, 1), [Part(site, quantity: 10)]);
        await AllocateAsync(admin, invoice, LineId(view, 2), [Part(site, quantity: 5)]);
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "Итого", 1830m) }));
        await OkAsync(await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}",
            new { requisites = await RequisitesWithAsync(admin, invoice, "ВТомЧислеНДС", 330m) }));

        var read = await ReadAsync(admin, invoice);
        Assert.Equal(0m, read.GetProperty("allocation").GetProperty("discrepancy").GetDecimal());
        Assert.Empty(read.GetProperty("readiness").GetProperty("gaps").EnumerateArray());
        await OkAsync(await admin.PostAsync($"/api/costs/invoices/{invoice}/parsed", null));

        await PayAsync(admin, invoice, today, await PreviewAsync(admin, invoice, today), null);

        async Task<decimal> CostsAsync(string vat) =>
            (await admin.GetFromJsonAsync<JsonElement>($"/api/costs/site-costs?site={site}&from={today:yyyy-MM}&to={today:yyyy-MM}{vat}"))
            .GetProperty("total").GetProperty("amount").GetDecimal();
        Assert.Equal(1830m, await CostsAsync(""));
        Assert.Equal(1500m, await CostsAsync("&vat=without"));
    }
}
