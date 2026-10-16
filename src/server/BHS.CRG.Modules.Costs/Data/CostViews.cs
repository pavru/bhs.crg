using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Чем различаются два взгляда на показанном отрезке — текст полосы отчёта (spec v3.3 п. 1.7).</summary>
public enum ViewsDiffer
{
    /// <summary>Взгляды равны в каждом зерне: полосы нет.</summary>
    None,

    /// <summary>Итоги равны, а различаются разделы, поставщики или дни.</summary>
    Details,

    /// <summary>Различаются итоги — с НДС или без.</summary>
    Totals,
}

/// <summary>
/// Два взгляда на деньги (E2, issue #1100, spec v3.3 п. 1.7).
///
/// <para><b>«С исправлениями»</b> — основная таблица, как сегодня: равен реестру и книгу не читает (I3).
/// <b>«На момент закрытия»</b> — у счёта с книгой строки книги всеми записями по их дням, у счёта без
/// книги — те же зёрна основной таблицы. Закрытый месяц во втором взгляде показывает ровно то, что было
/// в нём при закрытии, а исправление — днём, когда его сделали.</para>
///
/// <para>⚠️ <b>Строки книги берутся у ЛЮБОГО счёта с книгой</b>, а не только у оплаченного и не
/// отклонённого: книга — история, и у неоплаченного счёта она тоже лежит по дням (сентябрь 120 и
/// октябрь −120, spec п. 1.6). Отбери их запросом основной таблицы — закрытый месяц потерял бы деньги,
/// которые в нём были при закрытии.</para>
/// </summary>
public static class CostViews
{
    /// <summary>
    /// Взгляд «на момент закрытия»: счета без книги — зёрнами основной таблицы, счета с книгой — её
    /// строками.
    /// </summary>
    /// <param name="invoices">Счета основной таблицы — те же, что у взгляда «с исправлениями».</param>
    /// <param name="book">Строки книги отрезка (<see cref="BookAsync" />).</param>
    public static IReadOnlyList<CostMoney> Closing(
        IEnumerable<CostInvoice> invoices, IReadOnlyDictionary<Guid, LineVat> lines, IReadOnlyList<CostMoney> book) =>
        [.. SiteCosts.Corrected(invoices.Where(i => !i.Booked), lines), .. book];

    /// <summary>
    /// Строки книги, чей день в отрезке, — у стройки только её ячейки (раздел — на своей стройке).
    /// «Без позиции номенклатуры» — по счёту как он есть: так его найдёт реестр.
    /// </summary>
    public static async Task<IReadOnlyList<CostMoney>> BookAsync(
        CostsDbContext db, DateOnly first, DateOnly through, Guid? site, CancellationToken ct)
    {
        var rows = await (
                from line in db.InvoiceBookLines.AsNoTracking()
                join entry in db.InvoiceBookEntries.AsNoTracking() on line.EntryId equals entry.Id
                where line.Day >= first && line.Day <= through && (site == null || line.ConstructionId == site)
                select new { entry.InvoiceId, Line = line })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        // Один запрос на счета, а не подзапрос на каждую строку: у счёта строк книги много, а при первом
        // закрытии компании читается вся книга.
        var owners = rows.Select(r => r.InvoiceId).Distinct().ToList();
        var unmatched = (await db.InvoiceLines.AsNoTracking()
                .Where(l => owners.Contains(l.InvoiceId) && l.NomenclatureId == null)
                .Select(l => l.InvoiceId).Distinct().ToListAsync(ct))
            .ToHashSet();

        return [.. rows.Select(r => new CostMoney(r.InvoiceId, unmatched.Contains(r.InvoiceId), r.Line.Value))];
    }

    /// <summary>
    /// Различаются ли взгляды на отрезке и в охвате отчёта (spec п. 1.7). Сравниваются СУММЫ зерна —
    /// ячейки с разделом и поставщиком в один день — и только с НДС и без: строки книги одного зерна
    /// складываются, а «НДС неизвестен» в равенствах не участвует (п. 1.3 шаг 4). Пары — той же функцией,
    /// что у проводки (<see cref="InvoiceBookPosting.Pairs" />): зерно, сложившееся в ноль, — не зерно, и
    /// строки книги «+120 и −120 в один день» дают то же, что пустая основная таблица.
    /// </summary>
    public static ViewsDiffer Differ(
        IReadOnlyList<CostMoney> corrected, IReadOnlyList<CostMoney> closing, DateOnly from, DateOnly through, Guid? site)
    {
        Dictionary<(BookCell Cell, DateOnly Day), (decimal Gross, decimal Net)> Grains(IEnumerable<CostMoney> money) =>
            InvoiceBookPosting.Pairs(money.Select(m => m.Grain)
                    .Where(g => g.Day >= from && g.Day <= through && (site is null || g.Cell.Target?.ConstructionId == site)))
                .ToDictionary(p => p.Key, p => (p.Value.Gross, p.Value.Net));

        var (left, right) = (Grains(corrected), Grains(closing));
        if (left.Values.Sum(v => v.Gross) != right.Values.Sum(v => v.Gross)
            || left.Values.Sum(v => v.Net) != right.Values.Sum(v => v.Net))
            return ViewsDiffer.Totals;

        return left.Count == right.Count && left.All(g => right.TryGetValue(g.Key, out var other) && other == g.Value)
            ? ViewsDiffer.None
            : ViewsDiffer.Details;
    }
}
