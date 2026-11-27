using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

public partial class InvoicePaymentTests
{
    [Fact]
    public async Task InvoiceRequestLink_исправление_периода_не_переносит_связанный_счёт_на_другую_стройку()
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

        var view = await ReadAsync(admin, invoice);
        using var linked = await admin.PutAsJsonAsync($"/api/costs/invoices/{invoice}", new
        {
            requisites = view.GetProperty("requisites"), purchaseRequestId = request.Id,
        });
        await OkAsync(linked);
        var today = await TodayAsync();
        var paidOn = today.AddDays(-15);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        await CloseAsync(site, today.AddDays(-10));

        view = await ReadAsync(admin, invoice);
        var version = view.GetProperty("version").GetString();
        var moved = Correction(view, "Перенос на другой объект", lines =>
        {
            var parts = (Dictionary<string, object?>[])lines[0]["parts"]!;
            parts[0]["construction"] = other;
        });
        using var preview = await CorrectionRawAsync(admin, invoice, "preview", version, moved);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Contains("другая стройка", await ErrorAsync(preview));

        var sameSite = Correction(view, "Уточнено наименование", lines => lines[0]["supplierText"] = "Кабель");
        var accepted = await CorrectionPreviewAsync(admin, invoice, view, sameSite);
        var stamp = accepted.GetProperty("stamp").GetString();
        using var refused = await CorrectionRawAsync(admin, invoice, "", version, moved with { Stamp = stamp });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var unchanged = await ReadAsync(admin, invoice);
        Assert.Equal(version, unchanged.GetProperty("version").GetString());
        Assert.Equal(request.Id, unchanged.GetProperty("purchaseRequestId").GetGuid());
        await CorrectAsync(admin, invoice, view, sameSite with { Stamp = stamp });
    }
}
