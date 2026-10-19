using System.Net;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// НДС у части строк разобранного счёта (issue #1445) — тот же класс, что исправление
/// (<c>InvoicePaymentTests.Correction.cs</c>), отдельным файлом по занятию. Счёт — <c>LockedAsync</c>
/// разобранным: две строки без НДС, оплачен и заперт закрытием.
/// </summary>
public partial class InvoicePaymentTests
{
    /// <summary>
    /// НДС у всех строк или ни у одной держит «разобран» и в исправлении: назвать НДС у одной строки из
    /// молчавших — «хуже, чем было» (отчёт учёл бы вторую строку полной суммой), у обеих — можно.
    /// </summary>
    [Fact]
    public async Task Исправление_разобранного_не_вводит_НДС_у_части_строк()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, _, _) = await LockedAsync(admin, today, parsed: true);
        var view = await ReadAsync(admin, invoice);
        var version = view.GetProperty("version").GetString();

        var partial = await CorrectionRawAsync(admin, invoice, "preview", version,
            Correction(view, "НДС из бумаги", lines => lines[0]["vatRate"] = 20m));
        Assert.Equal(HttpStatusCode.BadRequest, partial.StatusCode);
        Assert.Contains("у строки 2 не указан НДС, а у других строк счёта указан", await ErrorAsync(partial));

        await CorrectionPreviewAsync(admin, invoice, view,
            Correction(view, "НДС из бумаги", lines => lines.ForEach(l => l["vatRate"] = 20m)));
    }
}
