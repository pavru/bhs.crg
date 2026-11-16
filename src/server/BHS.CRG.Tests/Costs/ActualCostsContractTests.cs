using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Periods;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Costs;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Costs;

/// <summary>
/// Контракт «Фактические затраты» на живом хосте со счетами (change <c>plan-24-actual-costs-contract</c>, ТЗ
/// CORE-34, PLAN-21.1, COST-21, COST-28, AUTH-8.1).
///
/// <para>Главный сторож — <b>равенство отчёту на одном посеве</b>: «оплачено» — «Затраты» отчёта «Затраты по
/// стройке» за всё время, «разнесено» — они же и «К оплате». Контракт берётся тем же путём, что у потребителя,
/// — портом <see cref="IModuleContracts" />; у каждого теста своя стройка: база у хоста общая.</para>
///
/// <para>Ломается так: поставщик отбрасывает черновики — «оплачено» меньше «Затрат» отчёта на оплаченный
/// черновик; берёт отклонённые — «разнесено» больше «Затрат» и «К оплате»; не сужает раздел — у раздела суммы
/// всей стройки.</para>
/// </summary>
[Collection("Integration")]
public class ActualCostsContractTests(InvoiceLineHost host) : InvoiceLineTestBase(host)
{
    /// <summary>Первый день календаря отчёта (<c>InvoicePeriods.First</c>, он внутренний): январь 2020-го.</summary>
    private static readonly DateOnly CalendarStart = new(2020, 1, 1);

    [Fact]
    public async Task Разнесено_и_оплачено_равны_отчёту_по_стройке_черновик_входит_отклонённый_нет()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (site, sections) = await SiteAsync("Итоги", "1 эт.", "2 эт.");
        // Оплаченный черновик: 40 000 на первый этаж — «разобран» ему не ставится.
        var paid = await InvoiceAsync(admin, site, sections[0], cable, 100, 400);
        await PayAsync(admin, paid);
        // Неоплаченный: 60 000 на второй этаж.
        await InvoiceAsync(admin, site, sections[1], conduit, 50, 1200);
        // Отклонённый: 25 000 — ни в один итог.
        var rejected = await InvoiceAsync(admin, site, sections[0], cable, 50, 500);
        await OkAsync(await admin.PostAsync($"/api/costs/invoices/{rejected}/rejected", null));

        var totals = await ReadAsync(site, null, CostsModule.TotalsRead);

        Assert.Equal(100_000m, totals.Allocated);
        Assert.Equal(40_000m, totals.Paid);
        var report = await ReportAsync(admin, site);
        Assert.Equal(report.Spent, totals.Paid);
        Assert.Equal(report.Spent + report.Payable, totals.Allocated);
    }

    /// <summary>
    /// «За всё время» — календарь отчёта (ревью PR #1993): дата оплаты снизу не ограничена, и счёт, оплаченный
    /// до первого месяца календаря, не показывает ни один отчёт — его нет и в итогах контракта.
    /// </summary>
    [Fact]
    public async Task Оплата_до_календаря_отчёта_не_входит_как_и_в_отчёт()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (site, sections) = await SiteAsync("Календарь", "1 эт.");
        var early = await InvoiceAsync(admin, site, sections[0], cable, 10, 500);
        await PayAsync(admin, early, CalendarStart.AddDays(-17));
        var current = await InvoiceAsync(admin, site, sections[0], cable, 10, 400);
        await PayAsync(admin, current);

        var totals = await ReadAsync(site, null, CostsModule.TotalsRead);

        Assert.Equal((4_000m, 4_000m), (totals.Allocated, totals.Paid));
        var report = await ReportAsync(admin, site);
        Assert.Equal(report.Spent, totals.Paid);
        Assert.Equal(report.Spent + report.Payable, totals.Allocated);
    }

    [Fact]
    public async Task Отбор_по_разделу_берёт_только_его_доли()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (site, sections) = await SiteAsync("Разделы", "1 эт.", "2 эт.");
        var invoice = await CreateAsync(admin, complete: true, total: 100_000m);
        var view = await LinesAsync(admin, invoice, [Line(cable, 100, 400), Line(conduit, 50, 1200)]);
        await AllocateAsync(admin, invoice, LineId(view, 1), [Part(site, quantity: 100, section: sections[0])]);
        await AllocateAsync(admin, invoice, LineId(view, 2), [Part(site, quantity: 50, section: sections[1])]);
        await PayAsync(admin, invoice);
        await InvoiceAsync(admin, site, sections[1], cable, 10, 300);

        var first = await ReadAsync(site, sections[0], CostsModule.TotalsRead);
        var second = await ReadAsync(site, sections[1], CostsModule.TotalsRead);
        var whole = await ReadAsync(site, null, CostsModule.TotalsRead);

        Assert.Equal((40_000m, 40_000m), (first.Allocated, first.Paid));
        Assert.Equal((63_000m, 60_000m), (second.Allocated, second.Paid));
        Assert.Equal((103_000m, 100_000m), (whole.Allocated, whole.Paid));
    }

    [Fact]
    public async Task Доля_по_работам_не_ведётся_а_не_ноль()
    {
        var (site, _) = await SiteAsync("Работы");

        var totals = await ReadAsync(site, null, CostsModule.TotalsRead);

        Assert.Equal(WorksLink.NotTrackedState, totals.LinkedToWorks.State);
        Assert.False(string.IsNullOrWhiteSpace(totals.LinkedToWorks.Reason));
        // Числа у доли нет вовсе — не ноль: ключа суммы в ней нет.
        var json = JsonSerializer.SerializeToElement(totals.LinkedToWorks, JsonSerializerOptions.Web);
        Assert.Equal(["state", "reason"], json.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Отвечает_праву_итогов_и_праву_отчётов_остальным_скрыто()
    {
        var (admin, _) = await SignInAsync("Admin");
        var (site, sections) = await SiteAsync("Права", "1 эт.");
        await InvoiceAsync(admin, site, sections[0], cable, 10, 400);

        Assert.Equal(4_000m, (await ReadAsync(site, null, CostsModule.TotalsRead)).Allocated);
        Assert.Equal(4_000m, (await ReadAsync(site, null, CostsModule.ReportRead)).Allocated);

        // Право на счета — не право на итоги: реестр он видит модулем, а контракт этого права не спрашивает.
        var hidden = Assert.IsType<ContractAnswer<ActualCostsTotals>.Hidden>(
            await ContractAsync(Reader("costs.invoice.read", "core.constructions.read"), site));
        Assert.Equal(CostsModule.TotalsRead, hidden.Permission);
    }

    [Fact]
    public async Task Системе_отказ_с_причиной()
    {
        var (site, _) = await SiteAsync("Система");

        var refused = Assert.IsType<ContractAnswer<ActualCostsTotals>.SystemRefused>(
            await ContractAsync(ModuleReader.OfSystem("ночная сводка затрат"), site));

        Assert.Equal("ночная сводка затрат", refused.Reason);
    }

    [Fact]
    public void Контракт_объявлен_с_поставщиком_costs_и_называет_права_ответа()
    {
        using var scope = host.Services.CreateScope();

        var declared = host.Services.GetRequiredService<ModuleContractCatalog>().Find(typeof(IActualCosts));
        Assert.NotNull(declared);
        Assert.Equal(CostsModule.ModuleCode, declared.Declaration.Provider);
        Assert.Equal("Фактические затраты", declared.Declaration.Title);
        var contract = Assert.IsType<ModuleContractAnswer<IActualCosts>.Connected>(
            scope.ServiceProvider.GetRequiredService<IModuleContracts>().Get<IActualCosts>()).Contract;
        Assert.Equal([CostsModule.TotalsRead, CostsModule.ReportRead], contract.AnswerPermissions);
    }

    // ── Помощники ─────────────────────────────────────────────────────────────

    /// <summary>Счёт из одной строки, разнесённой целиком на раздел стройки; сумма к оплате — сумма строки.</summary>
    private static async Task<Guid> InvoiceAsync(
        HttpClient client, Guid site, Guid section, Guid nomenclature, decimal quantity, decimal price)
    {
        var invoice = await CreateAsync(client, complete: true, total: quantity * price);
        var view = await LinesAsync(client, invoice, [Line(nomenclature, quantity, price)]);
        await AllocateAsync(client, invoice, LineId(view, 1), [Part(site, quantity: quantity, section: section)]);
        return invoice;
    }

    /// <summary>Оплата — сегодняшним днём или названным — через предпросмотр, как её вносит экран.</summary>
    private async Task PayAsync(HttpClient client, Guid invoice, DateOnly? paidOn = null)
    {
        DateOnly day;
        using (var scope = host.Services.CreateScope())
            day = paidOn ?? await scope.ServiceProvider.GetRequiredService<IPeriodClosures>().TodayAsync();
        var today = day.ToString("yyyy-MM-dd");

        var preview = await client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/paid/preview", new { paidOn = today });
        await OkAsync(preview);
        var seen = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stamp").GetString();
        await OkAsync(await client.PostAsJsonAsync($"/api/costs/invoices/{invoice}/paid", new { paidOn = today, document = (string?)null, seen }));
    }

    /// <summary>«Затраты» и «К оплате» отчёта по стройке — за весь его календарь, с первого месяца по текущий.</summary>
    private async Task<(decimal Spent, decimal Payable)> ReportAsync(HttpClient client, Guid site)
    {
        DateOnly today;
        using (var scope = host.Services.CreateScope())
            today = await scope.ServiceProvider.GetRequiredService<IPeriodClosures>().TodayAsync();

        var report = await client.GetFromJsonAsync<JsonElement>($"/api/costs/site-costs?site={site}&from={CalendarStart:yyyy-MM}&to={today:yyyy-MM}");
        return (report.GetProperty("total").GetProperty("amount").GetDecimal(),
            report.GetProperty("payable").GetProperty("amount").GetDecimal());
    }

    private async Task<ActualCostsTotals> ReadAsync(Guid site, Guid? section, params string[] keys) =>
        Assert.IsType<ContractAnswer<ActualCostsTotals>.Shown>(await ContractAsync(Reader(keys), site, section)).Value;

    private static ModuleReader Reader(params string[] keys) => ModuleReader.Of(Guid.NewGuid(), "Пробный Читатель", keys);

    private async Task<ContractAnswer<ActualCostsTotals>> ContractAsync(ModuleReader reader, Guid site, Guid? section = null)
    {
        using var scope = host.Services.CreateScope();
        var contract = Assert.IsType<ModuleContractAnswer<IActualCosts>.Connected>(
            scope.ServiceProvider.GetRequiredService<IModuleContracts>().Get<IActualCosts>()).Contract;
        return await contract.ReadAsync(reader, site, section);
    }
}
