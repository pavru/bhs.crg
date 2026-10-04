using System.Globalization;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Tables;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>Учётные месяцы счёта в реестре.</summary>
/// <param name="All">Все месяцы счёта — клетка «Учётный период»: это факт о счёте, отбор его не сужает.</param>
/// <param name="Named">Деньги по месяцам для «Сумм по периодам»: под отбором по объекту — только доли на
/// названные объекты, без остатка (остаток не лежит ни на одном объекте); иначе то же, что <paramref name="All" />.</param>
internal sealed record InvoiceMonths(IReadOnlyList<PostedMonth> All, IReadOnlyList<PostedMonth> Named);

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

    /// <summary>Деньги по месяцам — оплаченным счетам страницы; у неоплаченного учётных дат нет.</summary>
    /// <param name="loaded">Доли счетов страницы, если их уже прочитали ради другой колонки.</param>
    /// <param name="named">Отбор называет объекты: какие доли на них легли. null — объектов отбор не называет.</param>
    public static async Task<IReadOnlyDictionary<Guid, InvoiceMonths>> ReadAsync(
        CostsDbContext db, IReadOnlyList<Invoice> invoices, IReadOnlyList<InvoiceAllocation>? loaded,
        Func<InvoiceAllocation, bool>? named, CancellationToken ct)
    {
        var paid = invoices.Where(i => i.Payment == InvoicePaymentState.Paid).ToList();
        if (paid.Count == 0) return None;

        var ids = paid.Select(i => i.Id).ToList();
        var parts = (loaded ?? await db.InvoiceAllocations.AsNoTracking().Where(a => ids.Contains(a.InvoiceId)).ToListAsync(ct))
            .ToLookup(a => a.InvoiceId);
        var lines = (await db.InvoiceLines.AsNoTracking().Where(l => ids.Contains(l.InvoiceId))
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

    /// <summary>Клетка «Учётный период»: месяцы по возрастанию.</summary>
    public static IReadOnlyList<string> Cell(IReadOnlyList<PostedMonth> months) =>
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
