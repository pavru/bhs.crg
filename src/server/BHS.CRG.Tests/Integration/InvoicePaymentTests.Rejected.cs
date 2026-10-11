using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace BHS.CRG.Tests.Integration;

// «Отклонён» и оплата (issue #1284): две оси состояния счёта, которые обязаны не противоречить друг
// другу. Оплаченный не отклоняют, отклонённый не оплачивают — в обе стороны, одним и тем же выходом.
public partial class InvoicePaymentTests
{
    /// <summary>
    /// Оплаченный счёт отклонить нельзя: его деньги уже в затратах, и «отклонён» молча вынул бы их
    /// оттуда. Отказ называет выход — отменить оплату; после неё счёт отклоняется, и отметить оплату по
    /// нему уже нельзя.
    /// </summary>
    [Fact]
    public async Task Оплаченный_счёт_не_отклоняется_пока_оплата_не_отменена()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, _, _) = await TwoSitesAsync(admin);
        await PayAsync(admin, invoice, today, await PreviewAsync(admin, invoice, today), null);
        var before = (await ReadAsync(admin, invoice)).GetProperty("updatedAt").GetString();

        var refused = await admin.PostAsync($"/api/costs/invoices/{invoice}/rejected", null);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Сначала отмените оплату", await ErrorAsync(refused));
        Assert.Equal(before, (await ReadAsync(admin, invoice)).GetProperty("updatedAt").GetString());

        await OkAsync(await admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "решили не платить" }));
        var rejected = await admin.PostAsync($"/api/costs/invoices/{invoice}/rejected", null);
        await OkAsync(rejected);
        var view = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Отклонён", view.GetProperty("requisites").GetProperty("Состояние").GetString());

        // И обратное: отклонённый не оплачивают. Причину форма знает до нажатия — из ответа чтения.
        Assert.Equal("счёт отклонён", view.GetProperty("payment").GetProperty("refusal").GetString());
        var preview = await PreviewAsync(admin, invoice, today);
        Assert.Equal("счёт отклонён", preview.GetProperty("refusal").GetString());
    }
}
