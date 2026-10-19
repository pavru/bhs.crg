using System.Net;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        Assert.Contains("НДС указан не у всех строк — его нет у строки 2, а до исправления он был у всех или ни у одной",
            await ErrorAsync(partial));

        await CorrectionPreviewAsync(admin, invoice, view,
            Correction(view, "НДС из бумаги", lines => lines.ForEach(l => l["vatRate"] = 20m)));
    }

    /// <summary>
    /// Счёт, разобранный с частичным НДС до правила, исправлению не заперт (ревью PR #1524): «не хуже» меряется
    /// числом строк без НДС, а не парой (строка, вид), — перенести НДС на соседнюю строку можно, частичный НДС
    /// от этого не хуже.
    /// </summary>
    [Fact]
    public async Task Прежний_частичный_НДС_исправление_вправе_оставить()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, _, _) = await LockedAsync(admin, today, parsed: true);
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CostsDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE costs.invoice_lines SET vat_rate = 20, vat_amount = 6666.67 WHERE invoice_id = {invoice} AND ordinal = 1");
        var view = await ReadAsync(admin, invoice);

        await CorrectionPreviewAsync(admin, invoice, view,
            Correction(view, "НДС не у той строки", lines => lines[1]["vatRate"] = 20m));
    }
}
