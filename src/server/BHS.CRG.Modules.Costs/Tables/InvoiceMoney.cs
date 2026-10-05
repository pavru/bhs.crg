using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>Счёт — ровно то, что нужно арифметике денег.</summary>
internal sealed record InvoiceHead(Guid Id, decimal? Total, DateOnly? RemainderAccountingOn);

/// <summary>
/// Деньги счетов по частям — ЕДИНСТВЕННЫЙ читатель зерна «доля с деньгами» в модуле (задача G5, issue
/// #1098; ревизия Архитектора).
///
/// <para><b>Зачем один.</b> Деньги доли не хранятся (решение владельца 05.10.2026): их даёт арифметика
/// разноски (<see cref="AllocationMath" />) — с копейками округления и расхождением с суммой к оплате,
/// которые уходят в последнюю часть (ТЗ COST-13). Пропорция в запросе разошлась бы с ней. Пока доли
/// читали три места — «Сумма» под отбором, учётные месяцы и блок «Разноска», — каждое со своим
/// проходом, «сходится» держалось на том, что три прохода написаны одинаково. Теперь проход один: клетка
/// реестра, её расшифровка и «Затраты по стройке» складывают одни и те же строки, и сверка «реестр =
/// затраты» сверяет цифру, а не две реализации.</para>
///
/// <para><b>Цена.</b> Строки и части счетов читаются целиком; у итога — по ВСЕМУ отбору, а не по
/// странице. Отбор по объекту обычно сужает счета до одной стройки; отбор по учётному периоду так не
/// сужает («период содержит 2026» — счета года).</para>
/// </summary>
internal static class InvoiceMoney
{
    public static IReadOnlyDictionary<Guid, IReadOnlyList<PostedMoney>> None { get; } =
        new Dictionary<Guid, IReadOnlyList<PostedMoney>>();

    public static IReadOnlyList<InvoiceHead> Heads(IEnumerable<Invoice> invoices) =>
        [.. invoices.Select(i => new InvoiceHead(i.Id, i.Total, i.RemainderAccountingOn))];

    public static Task<List<InvoiceHead>> HeadsAsync(IQueryable<Invoice> invoices, CancellationToken ct) =>
        invoices.Select(i => new InvoiceHead(i.Id, i.Total, i.RemainderAccountingOn)).ToListAsync(ct);

    /// <param name="invoices">Счета страницы — либо ВСЕГО отбора, когда по ним считается итог.</param>
    /// <param name="owners">Те же счета запросом — когда это весь отбор: тысячи идентификаторов списком
    /// параметров в запрос не идут. null — счета страницы, их немного.</param>
    /// <param name="loaded">Части этих счетов, если их уже прочитали ради другой колонки.</param>
    /// <returns>По счёту — его деньги: части разноски и остаток (см. <see cref="PaymentPosting.Money" />).</returns>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PostedMoney>>> ReadAsync(
        CostsDbContext db, IReadOnlyList<InvoiceHead> invoices, IQueryable<Guid>? owners,
        IReadOnlyList<InvoiceAllocation>? loaded, CancellationToken ct)
    {
        if (invoices.Count == 0) return None;

        var ids = invoices.Select(i => i.Id).ToList();
        var of = owners ?? db.Invoices.Where(i => ids.Contains(i.Id)).Select(i => i.Id);
        var parts = (loaded ?? await db.InvoiceAllocations.AsNoTracking().Where(a => of.Contains(a.InvoiceId)).ToListAsync(ct))
            .ToLookup(a => a.InvoiceId);
        // Только то, что нужно арифметике: тексты строк счёта ей ни к чему, а читается, бывает, весь отбор.
        var lines = (await db.InvoiceLines.AsNoTracking().Where(l => of.Contains(l.InvoiceId))
                .Select(l => new { l.InvoiceId, l.Id, l.Ordinal, l.Quantity, l.Amount }).ToListAsync(ct))
            .ToLookup(l => l.InvoiceId, l => new AllocationLine(l.Id, l.Ordinal, l.Quantity, l.Amount));

        return invoices.ToDictionary(i => i.Id, i =>
        {
            IReadOnlyList<InvoiceAllocation> own = [.. parts[i.Id]];
            return PaymentPosting.Money(PaymentPosting.Balance(lines[i.Id], own, i.Total), i.Total, own, i.RemainderAccountingOn);
        });
    }

    /// <summary>
    /// Названы ли эти деньги отбором. Под отбором по учётному периоду названы только деньги с учётным
    /// днём — у неоплаченного счёта его нет, а доля без денег в затраты месяца не входит; под отбором
    /// только по объекту о периоде не спрашиваем вовсе: доля есть и у неоплаченного счёта.
    /// </summary>
    /// <param name="admitted">Вопрос к отбору: объект части (null — остаток) и её учётный день (null — о
    /// периоде не спрашиваем).</param>
    public static bool IsNamed(PostedMoney money, Func<InvoiceAllocation?, DateOnly?, bool> admitted, bool byPeriod) =>
        byPeriod
            ? money is { Amount: not null, AccountingOn: { } day } && admitted(money.Part, day)
            : admitted(money.Part, null);

    /// <summary>
    /// «Сумма» под сужающим отбором: деньги счёта, названные отбором. null — отбор не назвал из счёта
    /// ничего либо посчитать нечем (у названных частей нет суммы — в строке не вписана цена).
    /// </summary>
    public static decimal? Named(
        IReadOnlyList<PostedMoney> money, Func<InvoiceAllocation?, DateOnly?, bool> admitted, bool byPeriod)
    {
        var named = money.Where(m => m.Amount is not null && IsNamed(m, admitted, byPeriod)).ToList();
        return named.Count == 0 ? null : named.Sum(m => m.Amount!.Value);
    }
}
