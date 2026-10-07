using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>Счёт — ровно то, что нужно арифметике денег.</summary>
/// <param name="Paid">Оплачен ли: учётный день есть только у денег оплаченного счёта.</param>
internal sealed record InvoiceHead(Guid Id, decimal? Total, DateOnly? RemainderAccountingOn, bool Paid);

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
        [.. invoices.Select(i => new InvoiceHead(i.Id, i.Total, i.RemainderAccountingOn, i.Payment == InvoicePaymentState.Paid))];

    public static Task<List<InvoiceHead>> HeadsAsync(IQueryable<Invoice> invoices, CancellationToken ct) =>
        invoices.Select(i => new InvoiceHead(i.Id, i.Total, i.RemainderAccountingOn, i.Payment == InvoicePaymentState.Paid)).ToListAsync(ct);

    /// <param name="invoices">Счета страницы — либо ВСЕГО отбора, когда по ним считается итог.</param>
    /// <param name="owners">Те же счета запросом — когда это весь отбор: тысячи идентификаторов списком
    /// параметров в запрос не идут. null — счета страницы, их немного.</param>
    /// <param name="loaded">Части этих счетов, если их уже прочитали ради другой колонки (реестр — ради
    /// «Объекта»); null — читатель прочтёт сам (отчёт «Затраты по стройке»).</param>
    /// <returns>По счёту — его деньги: части разноски и остаток (см. <see cref="PaymentPosting.Money" />).
    /// ⚠️ Учётный день — только у денег ОПЛАЧЕННОГО счёта. Отмена оплаты даты стирает, но держаться на
    /// одном этом нельзя: дата, пережившая отмену (восстановленная копия, правка базы), назвала бы
    /// неоплаченному счёту учётный период и внесла бы его в затраты месяца (ревью PR #1199).</returns>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PostedMoney>>> ReadAsync(
        CostsDbContext db, IReadOnlyList<InvoiceHead> invoices, IQueryable<Guid>? owners,
        IReadOnlyList<InvoiceAllocation>? loaded, decimal tolerance, CancellationToken ct)
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
            var money = PaymentPosting.Money(PaymentPosting.Balance(lines[i.Id], own, i.Total, tolerance), i.Total, own, i.RemainderAccountingOn);
            return i.Paid ? money : [.. money.Select(m => m with { AccountingOn = null })];
        });
    }

    /// <summary>
    /// Названы ли эти деньги отбором — ОДНО правило на клетку «Сумма», её итог, «Суммы по периодам» и
    /// пометку «в отборе» расшифровки. Под отбором по учётному периоду названы только деньги с учётным
    /// днём — у неоплаченного счёта его нет, а доля без денег в затраты месяца не входит. Вне его доля
    /// есть и у неоплаченного счёта; но день, если он у денег есть, отбору называем всё равно: под
    /// «(объект А и период 09) или (объект Б)» период колонкой не назван, а октябрьская доля на А в
    /// первую ветку не входит (ревью PR #1199).
    /// </summary>
    /// <param name="admitted">Вопрос к отбору: объект части (null — остаток) и её учётный день (null —
    /// дня нет, о периоде не спрашиваем).</param>
    public static bool IsNamed(PostedMoney money, Func<InvoiceAllocation?, DateOnly?, bool> admitted, bool byPeriod) =>
        byPeriod
            ? money is { Amount: not null, AccountingOn: { } day } && admitted(money.Part, day)
            : admitted(money.Part, money.AccountingOn);

    /// <summary>
    /// «Сумма» под сужающим отбором: деньги счёта, названные отбором. null — отбор не назвал из счёта
    /// ничего либо посчитать нечем (у названных частей нет суммы — в строке не вписана цена).
    /// </summary>
    public static decimal? Named(IReadOnlyList<PostedMoney> money, Func<PostedMoney, bool> isNamed)
    {
        var named = money.Where(m => m.Amount is not null && isNamed(m)).ToList();
        return named.Count == 0 ? null : named.Sum(m => m.Amount!.Value);
    }
}
