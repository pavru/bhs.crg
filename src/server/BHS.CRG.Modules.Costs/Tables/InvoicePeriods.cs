using System.Globalization;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Tables;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>
/// Учётные месяцы счетов в реестре (задача C5, issue #1082, ТЗ COST-16, COST-20.1).
///
/// <para><b>Клетку считает та же функция, что и форму счёта</b> (<see cref="PaymentPosting.Months" />) —
/// в памяти, по счетам страницы. Деньги доли не хранятся: их даёт арифметика разноски, и пропорция в
/// запросе разошлась бы с ней на копейки округления.</para>
///
/// <para>⚠️ <b>Отбор и сортировка идут по ЗАПИСАННЫМ датам долей, а клетка — по долям с деньгами.</b>
/// Расходятся они на одном случае: у оплаченного счёта есть доля без денег (в строке не вписана цена,
/// разноска ждёт пересчёта) в месяце, куда больше ничего не легло. Такой счёт отбор по этому месяцу
/// найдёт, а в клетке месяца не будет. Посчитать «есть ли у доли деньги» запросом нельзя — по той же
/// причине, по какой нельзя посчитать долю.</para>
/// </summary>
internal static class InvoicePeriods
{
    /// <summary>Как назван месяц, которого нет среди записанных; на деле не встречается — перечень
    /// месяцев читается из тех же записей, что и условие.</summary>
    public const string Unknown = "период не определён";

    public static IReadOnlyDictionary<Guid, IReadOnlyList<PostedMonth>> None { get; } =
        new Dictionary<Guid, IReadOnlyList<PostedMonth>>();

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>
    /// Все учётные месяцы, какие есть в базе, — названиями: по ним ядро сверяет условие отбора и
    /// подставляет в запрос подошедшие ключи. Ключ месяца — число 202609: сравнивается и сортируется
    /// как есть.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, string>> LabelsAsync(CostsDbContext db, CancellationToken ct)
    {
        var shares = await db.InvoiceAllocations.AsNoTracking()
            .Where(a => a.AccountingOn != null)
            .Select(a => a.AccountingOn!.Value.Year * 100 + a.AccountingOn!.Value.Month)
            .Distinct().ToListAsync(ct);
        var remainders = await db.Invoices.AsNoTracking()
            .Where(i => i.RemainderAccountingOn != null)
            .Select(i => i.RemainderAccountingOn!.Value.Year * 100 + i.RemainderAccountingOn!.Value.Month)
            .Distinct().ToListAsync(ct);

        return shares.Union(remainders)
            .ToDictionary(key => key, key => PaymentViews.Month(new DateOnly(key / 100, key % 100, 1)));
    }

    /// <summary>Деньги по месяцам — оплаченным счетам страницы; у неоплаченного учётных дат нет.</summary>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PostedMonth>>> ReadAsync(
        CostsDbContext db, IReadOnlyList<Invoice> invoices, CancellationToken ct)
    {
        var paid = invoices.Where(i => i.Payment == InvoicePaymentState.Paid).ToList();
        if (paid.Count == 0) return None;

        var ids = paid.Select(i => i.Id).ToList();
        var parts = (await db.InvoiceAllocations.AsNoTracking().Where(a => ids.Contains(a.InvoiceId)).ToListAsync(ct))
            .ToLookup(a => a.InvoiceId);
        var lines = (await db.InvoiceLines.AsNoTracking().Where(l => ids.Contains(l.InvoiceId))
                .Select(l => new { l.InvoiceId, l.Id, l.Ordinal, l.Quantity, l.Amount }).ToListAsync(ct))
            .ToLookup(l => l.InvoiceId, l => new AllocationLine(l.Id, l.Ordinal, l.Quantity, l.Amount));

        return paid.ToDictionary(i => i.Id, i => PaymentPosting.Months(
            i.Total, lines[i.Id], [.. parts[i.Id]], i.RemainderAccountingOn));
    }

    /// <summary>Клетка «Учётный период»: месяцы по возрастанию.</summary>
    public static IReadOnlyList<string> Labels(IReadOnlyList<PostedMonth> months) =>
        [.. months.Select(m => PaymentViews.Month(m.Month))];

    /// <summary>
    /// Клетка «Суммы по периодам»: «40 000,00 (09.2026) + 60 000,00 (10.2026)»; пусто — счёт не оплачен.
    /// </summary>
    /// <param name="named">Условия отбора, называющие период; есть — в клетке только подошедшие месяцы.</param>
    public static string? Sums(IReadOnlyList<PostedMonth> months, IReadOnlyList<TableFilterCondition> named)
    {
        var shown = months
            .Select(m => (Label: PaymentViews.Month(m.Month), m.Amount))
            .Where(m => named.Count == 0 || named.Any(c => c.Matches(m.Label)))
            .Select(m => $"{m.Amount.ToString("N2", Russian)} ({m.Label})")
            .ToList();

        return shown.Count == 0 ? null : string.Join(" + ", shown);
    }
}
