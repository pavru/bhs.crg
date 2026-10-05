using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Tables;

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
/// <para>Раздел доли назван своей колонкой (задача G5b, issue #1198) — коротко, без стройки: она стоит
/// в соседней колонке. Под отбором по разделу помечены доли именно на него.</para>
/// </summary>
internal static class InvoiceBreakdown
{
    public const string ObjectKey = "Объект";
    public const string SectionKey = "Раздел";
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
            new(SectionKey, "Раздел", ModuleTableColumnKind.Text),
            new(ShareKey, "Доля", ModuleTableColumnKind.Number, Follows: InvoiceTable.AmountKey),
            new(MonthKey, "Учётный месяц", ModuleTableColumnKind.Text),
        ],
        [new(ShareKey, Named: InvoiceTable.AmountKey, Whole: InvoiceRequisites.TotalKey)]);

    /// <param name="money">Деньги счёта по частям — те же строки, из которых сложена клетка «Сумма»:
    /// поэтому «В отборе» расходиться с клеткой нечем.</param>
    /// <param name="named">Названы ли деньги отбором (<see cref="InvoiceMoney.IsNamed" />); вне сужающего
    /// отбора не спрашивается.</param>
    /// <param name="narrowed">Отбор сужает «Сумму» — называет объекты или учётные месяцы.</param>
    /// <returns>null — у счёта нет суммы к оплате: раскладывать нечего.</returns>
    public static TableRowBreakdown? Of(
        Invoice invoice, IReadOnlyList<PostedMoney> money, InvoiceShares shares, Func<PostedMoney, bool> named, bool narrowed)
    {
        if (invoice.Total is null) return null;
        bool Named(PostedMoney part) => narrowed && named(part);

        var rows = money.Where(m => m.Part is not null)
            .GroupBy(m => (Label: shares.Label(m.Part!), Section: shares.Section(m.Part!), Month: Month(m.AccountingOn)))
            .OrderBy(g => g.Key.Label, InvoiceShares.ByName).ThenBy(g => g.Key.Section, InvoiceShares.ByName).ThenBy(g => g.Key.Month)
            .Select(g => Row(g.Key.Label, Short(g.Key.Label, g.Key.Section),
                g.Any(m => m.Amount is not null) ? g.Sum(m => m.Amount ?? 0) : null, g.Key.Month, g.Any(Named)))
            .ToList();

        // Остаток существует только из-за денег, и само его название — факт о суммах («строки больше
        // суммы к оплате»): тому, кому суммы закрыты, строка не приходит (ревью PR #1197). У счёта без
        // разноски она есть и при нулевой сумме: иначе в блоке не было бы ни одной строки.
        var allocated = rows.Count > 0;
        var rest = money.Where(m => m.Part is null).ToList();
        if (rest.Count == 0 && !allocated) rest.Add(new(null, 0m, invoice.RemainderAccountingOn));
        rows.AddRange(rest.Select(m => Row(
            !allocated ? NotAllocated : m.Amount < 0 ? Correction : Unallocated, null, m.Amount, Month(m.AccountingOn), Named(m)) with
        {
            Follows = InvoiceTable.AmountKey,
        }));

        return new(rows, narrowed, invoice.Payment == InvoicePaymentState.Paid ? null : UnpaidNote);
    }

    private static DateOnly? Month(DateOnly? day) => day is { } on ? PaymentPosting.MonthOf(on) : null;

    /// <summary>Раздел без названия стройки: в панели она стоит в соседней колонке той же строки.</summary>
    private static string? Short(string site, string? section) =>
        section?.StartsWith(InvoiceShares.SectionLabel(site, ""), StringComparison.Ordinal) == true
            ? section[InvoiceShares.SectionLabel(site, "").Length..]
            : section;

    private static TableBreakdownRow Row(string label, string? section, decimal? amount, DateOnly? month, bool named) => new(
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ObjectKey] = label,
            [SectionKey] = section,
            [ShareKey] = amount,
            [MonthKey] = month is { } on ? PaymentViews.Month(on) : null,
        }, named);
}
