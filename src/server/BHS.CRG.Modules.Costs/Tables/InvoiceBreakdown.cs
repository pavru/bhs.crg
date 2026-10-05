using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Tables;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>
/// «Разноска» счёта в боковой панели строки реестра (ТЗ COST-20.1: счёт, разнесённый на несколько
/// строек, «раскрывается по ним»; задача G4, issue #1097).
///
/// <para>Строка — объект и учётный месяц: одна стройка бывает в двух месяцах, если её доли оплаченного
/// счёта развело закрытие периода. Блок показывает счёт ЦЕЛИКОМ при любом отборе; что из него отбор
/// назвал, говорит пометка строки — и сумма помеченных обязана равняться клетке «Сумма» (сверяет
/// ядро, <see cref="TableBreakdownSum" />). Поэтому «названо» спрашивается тем же вопросом к отбору,
/// что и у клетки.</para>
///
/// <para>Разделов стройки здесь нет — как и в колонке «Объект»: за ними идут в форму счёта.</para>
/// </summary>
internal static class InvoiceBreakdown
{
    public const string ObjectKey = "Объект";
    public const string ShareKey = "Доля";
    public const string MonthKey = "УчётныйМесяц";

    /// <summary>Остаток счёта, не лёгший ни на один объект.</summary>
    public const string Unallocated = "Не разнесено";

    /// <summary>У счёта нет ни одной части разноски — вся сумма в остатке.</summary>
    public const string NotAllocated = "Счёт не разнесён";

    /// <summary>Отрицательный остаток — то же слово, что в раскладе оплаты в форме счёта.</summary>
    public const string Correction = "Поправка: строки больше суммы к оплате";

    public const string UnpaidNote = "счёт не оплачен — в затраты не вошёл";

    public static ModuleTableBreakdown Declaration { get; } = new(
        "Разноска",
        [
            new(ObjectKey, "Объект", ModuleTableColumnKind.Text),
            new(ShareKey, "Доля", ModuleTableColumnKind.Number, Follows: InvoiceTable.AmountKey),
            new(MonthKey, "Учётный месяц", ModuleTableColumnKind.Text),
        ],
        [new(ShareKey, Named: InvoiceTable.AmountKey, Whole: InvoiceRequisites.TotalKey)]);

    /// <param name="loaded">Доли счёта, если их уже прочитали ради колонки.</param>
    /// <param name="admitted">Назван ли отбором объект доли (null — остаток) в её учётный день; день
    /// null — о периоде не спрашиваем (см. <c>InvoiceTableRows</c>).</param>
    /// <param name="narrowed">Отбор сужает «Сумму» — называет объекты или учётные месяцы.</param>
    /// <param name="byPeriod">Отбор называет учётные месяцы: тогда названными бывают только деньги с
    /// учётным днём — как и в клетке.</param>
    /// <returns>null — у счёта нет суммы к оплате: раскладывать нечего.</returns>
    public static async Task<TableRowBreakdown?> ReadAsync(
        CostsDbContext db, Invoice invoice, IReadOnlyList<InvoiceAllocation>? loaded, InvoiceShares shares,
        Func<InvoiceAllocation?, DateOnly?, bool> admitted, bool narrowed, bool byPeriod, CancellationToken ct)
    {
        if (invoice.Total is not { } total) return null;

        var parts = loaded?.Where(p => p.InvoiceId == invoice.Id).ToList()
                    ?? await db.InvoiceAllocations.AsNoTracking().Where(a => a.InvoiceId == invoice.Id).ToListAsync(ct);
        var lines = (await db.InvoiceLines.AsNoTracking().Where(l => l.InvoiceId == invoice.Id)
                .Select(l => new { l.Id, l.Ordinal, l.Quantity, l.Amount }).ToListAsync(ct))
            .Select(l => new AllocationLine(l.Id, l.Ordinal, l.Quantity, l.Amount));
        var money = PaymentPosting.Balance(lines, parts, total).Money.ToDictionary(share => share.Id, share => share.Amount);

        // Доля без денег (в строке не вписана цена) под отбором по периоду не названа: в затраты месяца
        // она не входит, и в клетке её нет.
        bool Named(InvoiceAllocation? part, DateOnly? day, bool hasMoney) =>
            narrowed && (byPeriod ? day is not null && hasMoney && admitted(part, day) : admitted(part, null));

        var rows = parts
            .Select(p => (Part: p, Amount: money.GetValueOrDefault(p.Id), Month: Month(p.AccountingOn)))
            .GroupBy(p => (Label: shares.Label(p.Part), p.Month))
            .OrderBy(g => g.Key.Label, InvoiceShares.ByName).ThenBy(g => g.Key.Month)
            .Select(g => Row(g.Key.Label, g.Any(p => p.Amount is not null) ? g.Sum(p => p.Amount ?? 0) : null, g.Key.Month,
                g.Any(p => Named(p.Part, p.Part.AccountingOn, p.Amount is not null))))
            .ToList();

        // Остаток существует только из-за денег, и само его название — факт о суммах («строки больше
        // суммы к оплате»): тому, кому суммы закрыты, строка не приходит (ревью PR #1197). У счёта без
        // разноски она есть и при нулевой сумме: иначе в блоке не было бы ни одной строки.
        var rest = total - money.Values.Sum(amount => amount ?? 0);
        if (rest != 0 || parts.Count == 0)
            rows.Add(Row(parts.Count == 0 ? NotAllocated : rest < 0 ? Correction : Unallocated, rest,
                Month(invoice.RemainderAccountingOn), Named(null, invoice.RemainderAccountingOn, true)) with
            {
                Follows = InvoiceTable.AmountKey,
            });

        return new(rows, narrowed, invoice.Payment == InvoicePaymentState.Paid ? null : UnpaidNote);
    }

    private static DateOnly? Month(DateOnly? day) => day is { } on ? PaymentPosting.MonthOf(on) : null;

    private static TableBreakdownRow Row(string label, decimal? amount, DateOnly? month, bool named) => new(
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ObjectKey] = label,
            [ShareKey] = amount,
            [MonthKey] = month is { } on ? PaymentViews.Month(on) : null,
        }, named);
}
