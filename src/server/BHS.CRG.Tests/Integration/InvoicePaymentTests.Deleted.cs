using System.Net;
using System.Net.Http.Json;

namespace BHS.CRG.Tests.Integration;

// Удаление и оплата (issue #1282): оплаченный счёт не удаляется — его деньги уже в затратах.
public partial class InvoicePaymentTests
{
    /// <summary>
    /// Оплаченный счёт удалить нельзя, даже черновик: отказ называет выход — отменить оплату. Её снимает
    /// только право <c>costs.invoice.pay</c>, так что одним правом правки оплаченное не убрать. После
    /// отмены оплаты счёт — черновик без оплаты, и он удаляется.
    /// </summary>
    [Fact]
    public async Task Оплаченный_счёт_не_удаляется_пока_оплата_не_отменена()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, _, _) = await TwoSitesAsync(admin);
        await OkAsync(await admin.PostAsync($"/api/costs/invoices/{invoice}/draft", null));
        await PayAsync(admin, invoice, today, await PreviewAsync(admin, invoice, today), null);
        var before = (await ReadAsync(admin, invoice)).GetProperty("updatedAt").GetString();

        var refused = await admin.DeleteAsync($"/api/costs/invoices/{invoice}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Сначала отмените оплату", await ErrorAsync(refused));
        Assert.Equal(before, (await ReadAsync(admin, invoice)).GetProperty("updatedAt").GetString());

        await OkAsync(await admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "заведён по ошибке" }));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/costs/invoices/{invoice}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/costs/invoices/{invoice}")).StatusCode);
    }
}
