using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Exchange;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Контракт «Фактические затраты» у счёта с книгой исправлений (change <c>plan-24-actual-costs-contract</c>, ревью
/// PR #1993): довод, ради которого поставщик берёт арифметику отчёта, — «свой проход разошёлся бы с отчётом на
/// первой корректировке закрытого периода». Здесь эта корректировка есть.
/// </summary>
public partial class InvoicePaymentTests
{
    /// <summary>Первый день календаря отчёта (<c>InvoicePeriods.First</c>, он внутренний): январь 2020-го.</summary>
    private static readonly DateOnly CalendarStart = new(2020, 1, 1);

    /// <summary>
    /// Счёт <c>LockedAsync</c> (40 000 на А, 60 000 на Б, А закрыта после оплаты): отмена оплаты пишет книгу
    /// закрытого периода, новая оплата — ещё строки. На каждом шаге итоги обеих строек равны отчёту.
    /// </summary>
    [Fact]
    public async Task Фактические_затраты_равны_отчёту_и_у_счёта_с_книгой_исправлений()
    {
        var (admin, _) = await SignInAsync("Admin");
        var today = await TodayAsync();
        var (invoice, a, b) = await LockedAsync(admin, today);

        await SameAsReportAsync(admin, today, a, allocated: 40_000m, paid: 40_000m);
        await SameAsReportAsync(admin, today, b, allocated: 60_000m, paid: 60_000m);

        // Отмена оплаты: в «оплачено» счёта нет, его доли — «к оплате».
        await OkAsync(await admin.PostAsJsonAsync($"/api/costs/invoices/{invoice}/unpaid", new { reason = "ошиблись" }));
        await SameAsReportAsync(admin, today, a, allocated: 40_000m, paid: 0m);
        await SameAsReportAsync(admin, today, b, allocated: 60_000m, paid: 0m);

        // Новая оплата тем же днём: доля А ляжет в открытый день, книга держит три строки.
        var paidOn = today.AddDays(-15);
        await PayAsync(admin, invoice, paidOn, await PreviewAsync(admin, invoice, paidOn), null);
        await SameAsReportAsync(admin, today, a, allocated: 40_000m, paid: 40_000m);
        await SameAsReportAsync(admin, today, b, allocated: 60_000m, paid: 60_000m);
    }

    /// <summary>Итоги контракта — названные, и они же — «Затраты» и «Затраты» с «К оплате» отчёта за весь календарь.</summary>
    private async Task SameAsReportAsync(HttpClient admin, DateOnly today, Guid site, decimal allocated, decimal paid)
    {
        ActualCostsTotals totals;
        using (var scope = host.Services.CreateScope())
        {
            var contract = Assert.IsType<ModuleContractAnswer<IActualCosts>.Connected>(
                scope.ServiceProvider.GetRequiredService<IModuleContracts>().Get<IActualCosts>()).Contract;
            totals = Assert.IsType<ContractAnswer<ActualCostsTotals>.Shown>(await contract.ReadAsync(
                ModuleReader.Of(Guid.NewGuid(), "Пробный Читатель", [CostsModule.TotalsRead]), site)).Value;
        }
        var report = await admin.GetFromJsonAsync<JsonElement>(
            $"/api/costs/site-costs?site={site}&from={CalendarStart:yyyy-MM}&to={today:yyyy-MM}");
        var spent = report.GetProperty("total").GetProperty("amount").GetDecimal();
        var payable = report.GetProperty("payable").GetProperty("amount").GetDecimal();

        Assert.Equal((allocated, paid), (totals.Allocated, totals.Paid));
        Assert.Equal((spent + payable, spent), (totals.Allocated, totals.Paid));
    }
}
