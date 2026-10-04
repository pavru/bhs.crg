using System.Globalization;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Tables;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>Учётные месяцы счёта в реестре.</summary>
/// <param name="All">Все месяцы счёта — клетка «Учётный период»: это факт о счёте, отбор его не сужает.</param>
/// <param name="Named">Деньги по месяцам, НАЗВАННЫЕ отбором, — «Сумма» и её расшифровка «Суммы по
/// периодам»: под отбором по объекту — только доли на него, без остатка (остаток не лежит ни на одном
/// объекте), под отбором по учётному периоду — только названные месяцы. Отбор не сужает — то же, что
/// <paramref name="All" />.</param>
internal sealed record InvoiceMonths(IReadOnlyList<PostedMonth> All, IReadOnlyList<PostedMonth> Named);

/// <summary>Оплаченный счёт — ровно то, что нужно арифметике месяцев.</summary>
internal sealed record PaidInvoice(Guid Id, decimal? Total, DateOnly? RemainderAccountingOn);

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
/// найдёт, а в клетке месяца не будет — и «Сумма» под таким отбором у него пуста: в итог он не идёт, и
/// итог называет число счетов, вошедших в него, а не число строк отбора. Посчитать «есть ли у доли
/// деньги» запросом нельзя — по той же причине, по какой нельзя посчитать долю.</para>
/// </summary>
internal static class InvoicePeriods
{
    /// <summary>Как назван месяц вне <see cref="Labels(DateOnly)" /> — раньше первого года перечня.</summary>
    public const string Unknown = "период не определён";

    /// <summary>
    /// С какого года перечень месяцев. Учётная дата — день платежа или первый открытый день после
    /// закрытия, то есть не раньше начала работы системы; год взят с запасом.
    /// </summary>
    private const int FirstYear = 2020;

    public static IReadOnlyDictionary<Guid, InvoiceMonths> None { get; } = new Dictionary<Guid, InvoiceMonths>();

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>
    /// Ключ месяца: 202609. Число, а не дата: сравнивается и сортируется как есть.
    /// ⚠️ Та же формула — в выражении колонки (<c>InvoiceTableRows.Sql</c>): запрос считает ключ в
    /// базе, и вынести выражение в общий метод нельзя — его не перевести. Что обе формулы одна, держит
    /// тест реестра: разойдись они, отбор по месяцу перестал бы находить счёт.
    /// </summary>
    public static int Key(DateOnly date) => date.Year * 100 + date.Month;

    /// <summary>
    /// Названия месяцев по ключам — по ним ядро сверяет условие отбора и подставляет в запрос подошедшие
    /// ключи. Перечень — календарь, а не чтение базы (ревью PR #1192): название месяца — чистая функция
    /// ключа, и два прохода по долям и счетам на КАЖДОЕ чтение таблицы были платой ни за что.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Labels(DateOnly today)
    {
        var labels = new Dictionary<int, string>();
        // До конца следующего года: учётная дата бывает позже «сегодня» только на первый открытый день.
        for (var month = new DateOnly(FirstYear, 1, 1); month.Year <= today.Year + 1; month = month.AddMonths(1))
            labels[Key(month)] = PaymentViews.Month(month);
        return labels;
    }

    /// <summary>
    /// Как месяц назван ОТБОРУ: названием из перечня, а вне перечня — <see cref="Unknown" />. То же
    /// название ядро сверяет с условием, отбирая счета, — иначе счёт с месяцем вне календаря отбор
    /// нашёл бы, а деньги его под тем же условием не подошли бы.
    /// </summary>
    public static string Label(IReadOnlyDictionary<int, string> labels, DateOnly day) =>
        labels.TryGetValue(Key(day), out var label) ? label : Unknown;

    /// <summary>Оплаченные из уже прочитанных счетов страницы; у неоплаченного учётных дат нет.</summary>
    public static IReadOnlyList<PaidInvoice> Paid(IEnumerable<Invoice> invoices) =>
        [.. invoices.Where(i => i.Payment == InvoicePaymentState.Paid)
            .Select(i => new PaidInvoice(i.Id, i.Total, i.RemainderAccountingOn))];

    /// <summary>Деньги по месяцам — оплаченным счетам.</summary>
    /// <param name="paid">Счета страницы — либо ВСЕГО отбора, когда по ним считается итог «Суммы»:
    /// посчитать деньги месяца запросом нельзя, как и долю (см. <see cref="InvoiceShares" />).</param>
    /// <param name="owners">Те же счета запросом — когда это весь отбор: тысячи идентификаторов списком
    /// параметров в запрос не идут (ревью PR #1195). null — счета страницы, их немного.</param>
    /// <param name="loaded">Доли этих счетов, если их уже прочитали ради другой колонки.</param>
    /// <param name="named">Какие деньги отбор назвал (см. <see cref="PaymentPosting.Months" />); null —
    /// отбор не сужает.</param>
    public static async Task<IReadOnlyDictionary<Guid, InvoiceMonths>> ReadAsync(
        CostsDbContext db, IReadOnlyList<PaidInvoice> paid, IQueryable<Guid>? owners,
        IReadOnlyList<InvoiceAllocation>? loaded, Func<InvoiceAllocation?, DateOnly, bool>? named, CancellationToken ct)
    {
        if (paid.Count == 0) return None;

        var ids = paid.Select(i => i.Id).ToList();
        var of = owners ?? db.Invoices.Where(i => ids.Contains(i.Id)).Select(i => i.Id);
        var parts = (loaded ?? await db.InvoiceAllocations.AsNoTracking()
                .Where(a => of.Contains(a.InvoiceId)).ToListAsync(ct))
            .ToLookup(a => a.InvoiceId);
        // Только то, что нужно арифметике: тексты строк счёта ей ни к чему, а читается, бывает, весь отбор.
        var lines = (await db.InvoiceLines.AsNoTracking()
                .Where(l => of.Contains(l.InvoiceId))
                .Select(l => new { l.InvoiceId, l.Id, l.Ordinal, l.Quantity, l.Amount }).ToListAsync(ct))
            .ToLookup(l => l.InvoiceId, l => new AllocationLine(l.Id, l.Ordinal, l.Quantity, l.Amount));

        return paid.ToDictionary(i => i.Id, i =>
        {
            IReadOnlyList<InvoiceAllocation> own = [.. parts[i.Id]];
            var balance = PaymentPosting.Balance(lines[i.Id], own, i.Total);
            var all = PaymentPosting.Months(balance, i.Total, own, i.RemainderAccountingOn);
            return new InvoiceMonths(all,
                named is null ? all : PaymentPosting.Months(balance, i.Total, own, i.RemainderAccountingOn, named));
        });
    }

    /// <summary>
    /// «Сумма» под отбором, называющим учётный период (задача G4, issue #1097): деньги счёта, вошедшие в
    /// названные месяцы; под отбором ещё и по объекту — только доли на него. Складываются РОВНО те
    /// месяцы, что стоят в «Суммах по периодам» (<see cref="InvoiceMonths.Named" />), — число и его
    /// расшифровка разойтись не могут: условие «названо отбором» у них одно.
    /// </summary>
    /// <returns>По счёту — деньги; null — в названные месяцы у счёта не вошло ничего.</returns>
    public static IReadOnlyDictionary<Guid, decimal?> Amounts(IReadOnlyDictionary<Guid, InvoiceMonths> periods) =>
        periods.ToDictionary(p => p.Key,
            p => p.Value.Named.Count == 0 ? (decimal?)null : p.Value.Named.Sum(m => m.Amount));

    /// <summary>Клетка «Учётный период»: месяцы по возрастанию.</summary>
    public static IReadOnlyList<string> Cell(IReadOnlyList<PostedMonth> months) =>
        [.. months.Select(m => PaymentViews.Month(m.Month))];

    /// <summary>
    /// Клетка «Суммы по периодам»: «40 000,00 (09.2026) + 60 000,00 (10.2026)»; пусто — счёт не оплачен
    /// либо отбор не назвал ни одного его месяца.
    /// </summary>
    public static string? Sums(IReadOnlyList<PostedMonth> months) => months.Count == 0
        ? null
        : string.Join(" + ", months.Select(m => $"{m.Amount.ToString("N2", Russian)} ({PaymentViews.Month(m.Month)})"));
}
