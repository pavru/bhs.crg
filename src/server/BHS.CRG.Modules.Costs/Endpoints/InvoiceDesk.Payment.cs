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
/// оплаты такого счёта (оплата И исправление) проверяет связка — входом <see cref="WhenLocked.Correct" />, до
/// версии формы; другой вход снять оплату такому счёту не может.</para>
/// </summary>
public sealed partial class InvoiceDesk
{
    /// <summary>
    /// Деньги счёта зёрнами по названным строкам и долям — при любом допуске: страховке нужны деньги и
    /// при допуске последней записи книги (spec п. 1.3 шаг 2), а узнать его можно только при записи.
    /// </summary>
    /// <remarks>Шапка снимается здесь же, значениями: после правки у отслеживаемого счёта её уже не будет.</remarks>
    private static Func<decimal, IReadOnlyList<Grain>> MoneyOf(
        Invoice invoice, IReadOnlyList<InvoiceLine> lines, IReadOnlyList<InvoiceAllocation> parts)
    {
        var head = (invoice.SupplierId, invoice.Total, invoice.VatTotal, invoice.RemainderAccountingOn);
        return tolerance => Grains(invoice.Id, head, lines, parts, tolerance);
    }

    /// <summary>
    /// Провести по книге отметку оплаты или её отмену — ПОСЛЕ того, как учётные дни легли (или стёрлись):
    /// оплата кладёт деньги теми днями, что назначил <see cref="PaymentPosting" />.
    /// </summary>
    /// <param name="before">Деньги до правки; у неоплаченного счёта их нет — <c>null</c>.</param>
    /// <param name="parts">Доли счёта, как они только что легли, — с учётными днями.</param>
    /// <param name="reason">Причина от человека (у отмены оплаты): книга — история закрытых месяцев, и
    /// «почему деньги ушли» обязано лежать в ней, а не только в журнале ядра (ревью PR #1367).</param>
    private async Task BookPaymentAsync(
        Invoice invoice, Func<decimal, IReadOnlyList<Grain>>? before, IReadOnlyList<InvoiceAllocation> parts,
        string? reason, PeriodBoundaries boundaries, CancellationToken ct)
    {
        var paid = invoice.Payment == InvoicePaymentState.Paid;
        var tolerance = await ToleranceAsync(ct);
        IReadOnlyList<Grain> Before(decimal t) => before?.Invoke(t) ?? [];
        // Строк оплата не меняет; у неоплаченного счёта они прочитаны не были — читаются здесь, один раз.
        var after = paid
            ? MoneyOf(invoice,
                await db.InvoiceLines.AsNoTracking().Where(l => l.InvoiceId == invoice.Id).ToListAsync(ct), parts)(tolerance)
            : [];

        await InvoiceBook.AppendAsync(db, invoice,
            last => new BookChange(paid ? InvoiceBookKind.Payment : InvoiceBookKind.Unpayment,
                Before(tolerance), tolerance, after, tolerance,
                last is { } t && t != tolerance ? Before(t) : null),
            boundaries, await clock.TodayAsync(ct), new BookAuthor(user.Id, reason), ct);
        await db.SaveChangesAsync(ct);
    }
}
