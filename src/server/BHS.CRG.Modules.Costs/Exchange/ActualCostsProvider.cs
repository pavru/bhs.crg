using System.Data;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Exchange;

/// <summary>
/// Поставщик контракта «Фактические затраты» (change <c>plan-24-actual-costs-contract</c>, ТЗ CORE-34,
/// PLAN-21.1, COST-21): два итога стройки для «Хода работ».
///
/// <para><b>Своего расчёта денег нет.</b> Счета отбираются теми же словами, что у отчёта «Затраты по стройке»
/// (не отклонён, доля на стройке), деньги читает тот же читатель (<see cref="SiteCostsEndpoints.InvoicesAsync" />
/// над <c>InvoiceMoney</c>), складывает та же арифметика: «оплачено» — «Затраты» <c>SiteCosts.Of</c>
/// за всё время во взгляде «с исправлениями», «разнесено» — они же и «К оплате» (<see cref="SiteCosts.Payable" />).
/// Свой проход разошёлся бы с отчётом на первой корректировке закрытого периода — а полоса и отчёт стоят
/// на соседних экранах.</para>
///
/// <para><b>Черновик — как в отчёте.</b> У черновика работают срок и оплата, и оплаченный черновик отчёт берёт
/// в «Затраты»: исключи его здесь — «оплачено» стало бы меньше отчёта на каждый счёт, оплаченный до разбора.
/// Вне итогов — только отклонённые.</para>
///
/// <para><b>Раздел</b> — тем же зерном: деньги доли с разделом отбора, остальное отбрасывается до арифметики
/// отчёта.</para>
/// </summary>
internal sealed class ActualCostsProvider(CostsDbContext db, IModuleSettings settings) : IActualCosts
{
    /// <summary>Итоги отдаются праву контракта и праву отчётов — тому, кто их видит и так.</summary>
    public IReadOnlyList<string> AnswerPermissions { get; } = [CostsModule.TotalsRead, CostsModule.ReportRead];

    public async Task<ContractAnswer<ActualCostsTotals>> ReadAsync(
        ModuleReader reader, Guid constructionId, Guid? sectionId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        // Система — отказом, человек без права — «скрыто» с главным правом; сумм в обоих нет.
        if (ContractAnswer.Refusal<ActualCostsTotals>(reader, [.. AnswerPermissions]) is { } refusal) return refusal;

        var tolerance = await settings.GetAsync(CostsSettings.AllocationTolerance, ct);
        var site = constructionId;
        var invoices = db.Invoices.AsNoTracking().Where(i =>
            i.State != InvoiceState.Rejected
            && db.InvoiceAllocations.Any(a => a.InvoiceId == i.Id && a.ConstructionId == site
                                              && (sectionId == null || a.SectionId == sectionId)));

        // Один снимок на оба чтения, как у отчёта: оплата, внесённая между ними, дала бы счёт дважды — в
        // «оплачено» и в «к оплате».
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : null;
        var (paid, paidLines) = await SiteCostsEndpoints.InvoicesAsync(
            db, invoices.Where(i => i.Payment == InvoicePaymentState.Paid), tolerance, ct);
        var (unpaid, unpaidLines) = await SiteCostsEndpoints.InvoicesAsync(
            db, invoices.Where(i => i.Payment != InvoicePaymentState.Paid), tolerance, ct);

        var money = SiteCosts.Corrected(paid, paidLines)
            .Where(m => sectionId is null || m.Grain.Cell.Target?.SectionId == sectionId)
            .ToList();
        var spent = SiteCosts.Of(money, DateOnly.MinValue, DateOnly.MaxValue, site, withVat: true).Total.Amount;

        IReadOnlyList<CostInvoice> waiting = sectionId is null
            ? unpaid
            : [.. unpaid.Select(i => i with { Money = [.. i.Money.Where(m => m.Part?.SectionId == sectionId)] })];
        var payable = SiteCosts.Payable(waiting, unpaidLines, site, withVat: true).Figure.Amount;

        return ContractAnswer.Shown(new ActualCostsTotals(spent + payable, spent, WorksLink.NotTracked));
    }
}
