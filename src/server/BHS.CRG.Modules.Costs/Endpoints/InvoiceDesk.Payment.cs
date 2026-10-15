using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Оплата и её отмена у счёта с книгой или запертого (E2, issue #1100, spec v3.3 п. 1.5) — проводкой по
/// книге, внутри связки записи.
///
/// <para><b>Почему в связке, а не в адресах оплаты.</b> Отметка оплаты меняет деньги счёта целиком: была
/// пустота — стала вся сумма, и наоборот. Книга, которая этого не узнала, разошлась бы с документом на всю
/// сумму (I1), а «на момент закрытия» показывал бы деньги, которых нет. Решает связка по тому, что видит
/// сама: сменилось «оплачен» — и у счёта есть книга либо он заперт. Адрес об этом не спрашивают.</para>
///
/// <para><b>Кто заводит книгу.</b> Отмена оплаты запертого — да: её деньги уходят из закрытого периода, и
/// след обязан остаться. Новая оплата — нет, только продолжает книгу, что уже есть: у неоплаченного счёта
/// закрытых денег не бывает. Незапертый счёт без книги — как до E2, книги не касается.</para>
///
/// <para>⚠️ <b>Версии нет</b>: оплата и отмена — события журнала, а не правка содержимого. Право на отмену
/// оплаты такого счёта проверяет адрес (оплата И исправление): связке права не известны.</para>
/// </summary>
public sealed partial class InvoiceDesk
{
    /// <summary>
    /// Деньги счёта зёрнами, как они лежат сейчас, — при любом допуске: страховке нужны деньги и при
    /// допуске последней записи книги (spec п. 1.3 шаг 2), а узнать его можно только при записи.
    /// </summary>
    /// <remarks>Шапка снимается здесь же, значениями: после правки у отслеживаемого счёта её уже не будет.</remarks>
    private async Task<Func<decimal, IReadOnlyList<Grain>>> GrainsAsync(
        Invoice invoice, IReadOnlyList<InvoiceAllocation> parts, CancellationToken ct)
    {
        var lines = await db.InvoiceLines.AsNoTracking().Where(l => l.InvoiceId == invoice.Id).ToListAsync(ct);
        var head = (invoice.SupplierId, invoice.Total, invoice.VatTotal, invoice.RemainderAccountingOn);
        return tolerance => Grains(invoice.Id, head, lines, parts, tolerance);
    }

    /// <summary>
    /// Провести по книге отметку оплаты или её отмену — ПОСЛЕ того, как учётные дни легли (или стёрлись):
    /// оплата кладёт деньги теми днями, что назначил <see cref="PaymentPosting" />.
    /// </summary>
    /// <param name="before">Деньги до правки; у неоплаченного счёта их нет — <c>null</c>.</param>
    private async Task BookPaymentAsync(
        Invoice invoice, Func<decimal, IReadOnlyList<Grain>>? before, PeriodBoundaries boundaries, CancellationToken ct)
    {
        var paid = invoice.Payment == InvoicePaymentState.Paid;
        var tolerance = await ToleranceAsync(ct);
        IReadOnlyList<Grain> Before(decimal t) => before?.Invoke(t) ?? [];
        var after = paid
            ? (await GrainsAsync(invoice,
                await db.InvoiceAllocations.AsNoTracking().Where(a => a.InvoiceId == invoice.Id).ToListAsync(ct), ct))(tolerance)
            : [];

        await InvoiceBook.AppendAsync(db, invoice,
            last => new BookChange(paid ? InvoiceBookKind.Payment : InvoiceBookKind.Unpayment,
                Before(tolerance), tolerance, after, tolerance,
                last is { } t && t != tolerance ? Before(t) : null),
            boundaries, await clock.TodayAsync(ct), new BookAuthor(user.Id), ct);
        await db.SaveChangesAsync(ct);
    }
}
