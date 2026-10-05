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
/// <para><b>Что считается.</b> Только дни, которые закрытие закрывает впервые. Неоплаченных счетов
/// здесь нет: они не принадлежат ни одному периоду (ТЗ COST-16).</para>
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

        var paid = SiteCostsEndpoints.Paid(db, first, scope.Through, site);
        var (invoices, lines) = await SiteCostsEndpoints.InvoicesAsync(db, paid, ct);
        CostFigure Money(IReadOnlyList<CostInvoice> of) =>
            SiteCosts.Of(of, lines, first, scope.Through, site, withVat: true).Total;

        // Не завершено: счёт не разобран либо у него остался неразнесённый остаток. Остаток — только
        // положительный: отрицательный — копеечная поправка к деньгам долей (строки чуть больше суммы к
        // оплате), а не «не разнесено» (решение владельца 05.10.2026). У стройки остатка нет: он лежит
        // на компании.
        var drafts = (await paid.Where(i => i.State != InvoiceState.Parsed).Select(i => i.Id).ToListAsync(ct)).ToHashSet();
        var unsettled = Money([.. invoices.Where(i =>
            drafts.Contains(i.Id) || (site is null && i.Money.Any(m => m.Part is null && m.Amount > 0)))]);
        var entering = Money(invoices);

        // Запирается счёт целиком и по ЛЮБОЙ закрытой учётной дате — и отклонённый оплаченный, и с долей
        // без денег, которых в затратах периода нет. Число называем, только когда оно другое.
        var locked = await SiteCostsEndpoints.Paid(db, first, scope.Through, site, rejectedToo: true).CountAsync(ct);

        List<ModuleClosingLine> unfinished = [], frozen = [];
        if (unsettled.Invoices > 0)
            unfinished.Add(new("unsettled",
                site is null ? "Оплачены в периоде, но не разнесены или не разобраны" : "Оплачены в периоде, но не разобраны",
                unsettled.Invoices, Invoices, unsettled.Amount, Amounts,
                "После закрытия эти деньги по стройкам останутся неверными."));
        if (entering.Invoices > 0)
            frozen.Add(new("entering", "Оплаченные счета, вошедшие в период", entering.Invoices, Invoices, entering.Amount, Amounts,
                "Их оплату, учётный период и разноску изменить будет нельзя. Неоплаченные счета к периоду не относятся и остаются открытыми."));
        if (locked != entering.Invoices)
            frozen.Add(new("locked", "Запрутся целиком", locked, Invoices,
                Note: "Вместе с отклонёнными оплаченными счетами и счетами, у которых в периоде есть доля без денег: " +
                      "счёт запирается целиком, если закрыта хоть одна его учётная дата."));

        return new(DateRule, unfinished, frozen);
    }
}
