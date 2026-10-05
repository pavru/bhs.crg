using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Раздел «Счета и накладные» в диалоге закрытия периода (задача E1b, issue #1099; ТЗ CORE-35, COST-16).
///
/// <para><b>Своего расчёта денег здесь нет.</b> Счета отбирает тот же запрос, деньги читает тот же
/// читатель и складывает та же функция, что у отчёта «Затраты по стройке»
/// (<see cref="SiteCostsEndpoints.Paid" />, <see cref="SiteCosts.Of" />): «войдут в закрытый период» за
/// целые месяцы равно итогу отчёта за них и итогу «Суммы» в реестре. Посчитай диалог сам — человек
/// закрывал бы период по одной цифре, а сверял по другой.</para>
///
/// <para><b>Что считается.</b> Только деньги, чей учётный день закрытие закрывает ВПЕРВЫЕ
/// (<see cref="ModuleClosingScope.ClosesAnew" />): при закрытии компании доля на стройку, закрытую своим
/// закрытием дальше, уже заперта и второй раз не называется (ревью PR #1201). Поэтому равенство с отчётом
/// «Затраты по стройке» — за дни, где таких строек нет. Неоплаченных счетов здесь нет: они не
/// принадлежат ни одному периоду (ТЗ COST-16).</para>
///
/// <para>⚠️ Замка записи отчёт НЕ берёт и через <c>InvoiceDesk</c> не ходит: при закрытии его зовут под
/// исключительным замком ядра (см. <see cref="IModuleClosingReport" />).</para>
/// </summary>
public sealed class CostsClosingReport(CostsDbContext db) : IModuleClosingReport
{
    public const string DateRule = "Счёт относится к периоду по учётному периоду оплаты. Накладных в системе пока нет.";

    /// <summary>Суммы счетов открывает право на отчёты по затратам — то же, что у «Затрат по стройке».</summary>
    private const string Amounts = "costs.report.read";

    private static readonly ModuleClosingUnit Invoices = new("счёт", "счёта", "счетов");

    public string Module => "costs";

    public async Task<ModuleClosingSection> ReportAsync(ModuleClosingScope scope, CancellationToken ct = default)
    {
        var first = scope.From ?? DateOnly.MinValue;
        var site = scope.ConstructionId;

        var (read, lines) = await SiteCostsEndpoints.InvoicesAsync(db, SiteCostsEndpoints.Paid(db, first, scope.Through, site), ct);
        // У каждого счёта оставлены только деньги впервые закрываемых дней — дальше всё считается по ним.
        IReadOnlyList<CostInvoice> invoices = [.. read.Select(i => i with
        {
            Money = [.. i.Money.Where(m => m.AccountingOn is { } day && scope.ClosesAnew(m.Part?.ConstructionId, day))],
        })];
        CostFigure Money(IReadOnlyList<CostInvoice> of) =>
            SiteCosts.Of(of, lines, first, scope.Through, site, withVat: true).Total;

        // Не завершено: счёт не разобран либо у него остался неразнесённый остаток. Остаток — только
        // положительный: отрицательный — копеечная поправка к деньгам долей (строки чуть больше суммы к
        // оплате), а не «не разнесено» (решение владельца 05.10.2026). И только остаток ЭТИХ дней: счёт
        // с долей в закрываемом месяце и остатком, перенесённым в следующий, здесь завершён. У стройки
        // остатка нет: он лежит на компании.
        var unsettled = Money([.. invoices.Where(i =>
            !i.Parsed || (site is null && i.Money.Any(m => m.Part is null && m.Amount > 0)))]);
        var entering = Money(invoices);
        var locked = await LockedAsync(scope, first, ct);

        List<ModuleClosingLine> unfinished = [], frozen = [];
        if (unsettled.Invoices > 0)
            unfinished.Add(new("unsettled",
                site is null ? "Оплачены в периоде, но не разнесены или не разобраны" : "Оплачены в периоде, но не разобраны",
                unsettled.Invoices, Invoices, unsettled.Amount, Amounts,
                // Сумма — не «сколько не разнесено», а деньги этих счетов: у неразобранного счёта
                // ненадёжна вся разноска, и делить строку на два вида сумм значило бы объяснять её дольше,
                // чем читать.
                "Сумма — деньги этих счетов в периоде. После закрытия их разноска по стройкам останется как есть."));
        if (entering.Invoices > 0)
            frozen.Add(new("entering", "Оплаченные счета, вошедшие в период", entering.Invoices, Invoices, entering.Amount, Amounts,
                "Их оплату, учётный период и разноску изменить будет нельзя. Неоплаченные счета к периоду не относятся и остаются открытыми."));
        if (locked != entering.Invoices)
            frozen.Add(new("locked", "Запрутся целиком", locked, Invoices,
                Note: "Вместе с отклонёнными оплаченными счетами и счетами, у которых в периоде есть доля без денег: " +
                      "счёт запирается целиком, если закрыта хоть одна его учётная дата."));

        return new(DateRule, unfinished, frozen);
    }

    /// <summary>
    /// Сколько счетов запрётся. Запирается счёт целиком и по ЛЮБОЙ закрытой учётной дате — и отклонённый
    /// оплаченный, и с долей без денег, которых в затратах периода нет.
    /// </summary>
    private async Task<int> LockedAsync(ModuleClosingScope scope, DateOnly first, CancellationToken ct)
    {
        var locked = SiteCostsEndpoints.Paid(db, first, scope.Through, scope.ConstructionId, rejectedToo: true);
        if (scope.ClosedAhead.Count == 0) return await locked.CountAsync(ct);

        // Есть стройки, закрытые дальше компании: «день в отрезке» ещё не значит «закрывается впервые», и
        // запросом это не выразить — учётные даты читаются и сверяются тем же правилом, что деньги.
        var owners = locked.Select(i => i.Id);
        var shares = await db.InvoiceAllocations.AsNoTracking()
            .Where(a => owners.Contains(a.InvoiceId) && a.AccountingOn >= first && a.AccountingOn <= scope.Through)
            .Select(a => new { a.InvoiceId, a.ConstructionId, a.AccountingOn }).ToListAsync(ct);
        var remainders = await locked.Where(i => i.RemainderAccountingOn >= first && i.RemainderAccountingOn <= scope.Through)
            .Select(i => i.Id).ToListAsync(ct);

        return shares.Where(a => scope.ClosesAnew(a.ConstructionId, a.AccountingOn!.Value)).Select(a => a.InvoiceId)
            .Concat(remainders).Distinct().Count();
    }
}
