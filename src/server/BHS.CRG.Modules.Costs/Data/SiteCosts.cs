namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Число отчёта: сколько счетов и сколько денег.</summary>
public sealed record CostFigure(int Invoices, decimal Amount);

/// <summary>Строка отчёта — стройка, статья вне строек или контрагент.</summary>
/// <param name="Id">Чья строка; null — «поставщик не указан».</param>
public sealed record CostLine(Guid? Id, string Name, int Invoices, decimal Amount);

/// <summary>Счёт так, как его видит отчёт о затратах.</summary>
/// <param name="VatTotal">«В том числе НДС» из шапки — запасной источник НДС там, где его нет у строки.</param>
/// <param name="Unmatched">Есть строки без позиции номенклатуры.</param>
public sealed record CostInvoice(
    Guid Id, Guid? SupplierId, decimal? Total, decimal? VatTotal, bool Unmatched, IReadOnlyList<PostedMoney> Money);

/// <summary>НДС строки счёта: сумма строки и сколько в ней НДС.</summary>
public sealed record LineVat(decimal? Amount, decimal? VatAmount);

/// <summary>Затраты за период — посчитанные; названий здесь нет, их даёт вызывающий.</summary>
/// <param name="Sites">По стройкам — на экране «Все стройки»; на экране стройки пусто.</param>
/// <param name="Articles">Статьи вне строек — там же, отдельной группой (ТЗ COST-10.1).</param>
/// <param name="Unallocated">Деньги оплаченных счетов, не лёгшие ни на один объект; null — таких нет.</param>
/// <param name="Suppliers">По контрагентам — на экране стройки; ключ null — «поставщик не указан».</param>
/// <param name="Unmatched">Из затрат — счета со строками без позиции номенклатуры; null — таких нет.</param>
/// <param name="VatUnknown">Под «без НДС»: деньги, из которых НДС вычесть нечем, — учтены полной
/// суммой; null — таких нет либо суммы показаны с НДС.</param>
public sealed record SiteCostsResult(
    IReadOnlyDictionary<Guid, CostFigure> Sites,
    IReadOnlyDictionary<Guid, CostFigure> Articles,
    CostFigure? Unallocated,
    IReadOnlyList<(Guid? Supplier, CostFigure Figure)> Suppliers,
    CostFigure Total,
    CostFigure? Unmatched,
    CostFigure? VatUnknown);

/// <summary>
/// «Затраты по стройке» (задача G5, issue #1098, ТЗ COST-20): деньги оплаченных счетов, вошедшие в
/// учётные месяцы периода.
///
/// <para><b>Складываются те же строки, что в реестре</b> (<see cref="PostedMoney" />): каждое число
/// отчёта ведёт в «Реестр счетов» с готовым отбором и обязано равняться итогу «Суммы» там. Отчёт,
/// посчитанный своим проходом, сверял бы с реестром реализацию, а не цифру.</para>
///
/// <para><b>Без НДС</b> — вычитанием: НДС части пропорционален её доле в строке. Нет НДС у строки —
/// берётся доля «в том числе НДС» из шапки счёта; нет и её — часть учтена ПОЛНОЙ суммой, и это названо
/// числом, а не спрятано (решение владельца 05.10.2026): «ставка не указана» — не «без НДС». Копейки
/// округляются у каждой части, поэтому сумма «без НДС» по частям может отличаться от «сумма минус НДС»
/// счёта на копейки.</para>
/// </summary>
public static class SiteCosts
{
    /// <param name="invoices">Оплаченные неотклонённые счета, у которых в период вошла хоть часть денег.</param>
    /// <param name="from">Первый день периода.</param>
    /// <param name="through">Последний день периода.</param>
    /// <param name="site">Стройка — тогда строки по контрагентам; null — все стройки.</param>
    /// <param name="withVat">Суммы как в бумаге; иначе — без НДС, где его есть чем вычесть.</param>
    public static SiteCostsResult Of(
        IReadOnlyList<CostInvoice> invoices, IReadOnlyDictionary<Guid, LineVat> lines,
        DateOnly from, DateOnly through, Guid? site, bool withVat)
    {
        var entries = invoices.SelectMany(i => i.Money
                .Where(m => m is { Amount: not null, AccountingOn: { } day } && day >= from && day <= through)
                .Where(m => site is null || m.Part?.ConstructionId == site)
                .Select(m =>
                {
                    var (amount, unknown) = withVat ? (m.Amount!.Value, false) : Net(m, i, lines);
                    return (Invoice: i, Money: m, Amount: amount, VatUnknown: unknown);
                }))
            .ToList();

        static CostFigure Figure<T>(IEnumerable<(CostInvoice Invoice, T Money, decimal Amount, bool VatUnknown)> rows)
        {
            var list = rows.ToList();
            return new(list.Select(r => r.Invoice.Id).Distinct().Count(), list.Sum(r => r.Amount));
        }
        static CostFigure? Some(CostFigure figure) => figure.Invoices == 0 ? null : figure;

        return new(
            site is null
                ? entries.Where(e => e.Money.Part?.ConstructionId is not null)
                    .GroupBy(e => e.Money.Part!.ConstructionId!.Value).ToDictionary(g => g.Key, Figure)
                : new Dictionary<Guid, CostFigure>(),
            site is null
                ? entries.Where(e => e.Money.Part?.ArticleId is not null)
                    .GroupBy(e => e.Money.Part!.ArticleId!.Value).ToDictionary(g => g.Key, Figure)
                : new Dictionary<Guid, CostFigure>(),
            site is null ? Some(Figure(entries.Where(e => e.Money.Part is null))) : null,
            site is null ? [] : [.. entries.GroupBy(e => e.Invoice.SupplierId).Select(g => (g.Key, Figure(g)))],
            Figure(entries),
            Some(Figure(entries.Where(e => e.Invoice.Unmatched))),
            withVat ? null : Some(Figure(entries.Where(e => e.VatUnknown))));
    }

    /// <summary>
    /// «К оплате»: неоплаченные неотклонённые счета — от периода не зависит, неоплаченный счёт не
    /// принадлежит ни одному (ТЗ COST-16). По всем стройкам — суммы к оплате; по стройке — доли на неё.
    /// </summary>
    public static CostFigure Payable(
        IReadOnlyList<CostInvoice> unpaid, IReadOnlyDictionary<Guid, LineVat> lines, Guid? site, bool withVat)
    {
        var amounts = unpaid.Select(i => site is null
                // Счёт целиком: его «без НДС» — сумма минус «в том числе НДС» из шапки, если она названа.
                ? (Invoice: i, Amount: (decimal?)((i.Total ?? 0) - (withVat ? 0 : i.VatTotal ?? 0)))
                : (Invoice: i, Amount: Share(i)))
            .Where(a => a.Amount is not null)
            .ToList();
        return new(amounts.Count, amounts.Sum(a => a.Amount!.Value));

        decimal? Share(CostInvoice invoice)
        {
            var mine = invoice.Money.Where(m => m.Amount is not null && m.Part?.ConstructionId == site).ToList();
            return mine.Count == 0 ? null : mine.Sum(m => withVat ? m.Amount!.Value : Net(m, invoice, lines).Amount);
        }
    }

    /// <summary>Деньги части без НДС — и признак, что вычесть было нечем.</summary>
    public static (decimal Amount, bool VatUnknown) Net(
        PostedMoney money, CostInvoice invoice, IReadOnlyDictionary<Guid, LineVat> lines)
    {
        var amount = money.Amount!.Value;
        // НДС строки — точнее шапки: в одном счёте бывают строки с разной ставкой.
        var (whole, vat) = money.Part?.LineId is { } line && lines.TryGetValue(line, out var own) && own is { Amount: not null and not 0, VatAmount: not null }
            ? (own.Amount, own.VatAmount)
            : (invoice.Total, invoice.VatTotal);

        return whole is { } basis && basis != 0 && vat is { } tax
            ? (amount - Math.Round(amount * tax / basis, 2, MidpointRounding.AwayFromZero), false)
            : (amount, true);
    }
}
