using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

public partial class InvoicePaymentTests
{
    [Fact]
    public async Task InvoiceRequestLink_после_снижения_допуска_исправляет_текст_на_той_же_стройке()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (invoice, _, site) = await InvoiceWithPositionAsync(admin, "Позиция связанного счёта");
        var (other, _) = await SiteAsync("Другая стройка для исправления");
        var request = PurchaseRequest.Create(site, Guid.NewGuid(), DateTimeOffset.UtcNow);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CostsDbContext>();
            db.PurchaseRequests.Add(request);
            db.Entry(request).Property(row => row.Status).CurrentValue = PurchaseRequestStatus.Submitted;
            await db.SaveChangesAsync();
        }

        var requisites = await RequisitesWithAsync(admin, invoice, "Итого", 40_000.50m);
        using var linked = await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new
        {
            requisites, purchaseRequestId = request.Id,
        });
        await OkAsync(linked);
        var today = await TodayAsync();
        var paidOn = today.AddDays(-15);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        await CloseAsync(site, today.AddDays(-10));

        try
        {
            await ToleranceAsync(admin, "0.10");
            var view = await ReadAsync(admin, invoice);
            var allocation = view.GetProperty("allocation");
            Assert.Equal(0.10m, allocation.GetProperty("tolerance").GetDecimal());
            Assert.Equal(0.50m, allocation.GetProperty("discrepancy").GetDecimal());
            Assert.False(allocation.GetProperty("withinTolerance").GetBoolean());
            var version = view.GetProperty("version").GetString();
            var moved = Correction(view, "Перенос на другой объект", lines =>
            {
                var parts = (Dictionary<string, object?>[])lines[0]["parts"]!;
                parts[0]["construction"] = other;
            });
            using var preview = await CorrectionRawAsync(admin, invoice, "preview", version, moved);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
            Assert.Contains("другая стройка", await ErrorAsync(preview));

            var worse = Correction(view, "Увеличить расхождение", total: 40_000.75m);
            using var worsePreview = await CorrectionRawAsync(admin, invoice, "preview", version, worse);
            Assert.Equal(HttpStatusCode.BadRequest, worsePreview.StatusCode);
            Assert.Contains("расходится", await ErrorAsync(worsePreview));

            var sameSite = Correction(view, "Уточнено наименование", lines => lines[0]["supplierText"] = "Кабель");
            var accepted = await CorrectionPreviewAsync(admin, invoice, view, sameSite);
            var stamp = accepted.GetProperty("stamp").GetString();
            using var refused = await CorrectionRawAsync(admin, invoice, "", version, moved with { Stamp = stamp });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var unchanged = await ReadAsync(admin, invoice);
            Assert.Equal(version, unchanged.GetProperty("version").GetString());
            var corrected = await CorrectAsync(admin, invoice, view, sameSite with { Stamp = stamp });
            Assert.Equal(request.Id, corrected.GetProperty("purchaseRequestId").GetGuid());
            Assert.Equal("Кабель", corrected.GetProperty("lines")[0].GetProperty("supplierText").GetString());
        }
        finally
        {
            await ToleranceAsync(admin, null);
        }
    }
}
