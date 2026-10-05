using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Tables;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>«Затраты по стройке» — ответ отчёта.</summary>
/// <param name="Site">Выбранная стройка; null — все стройки.</param>
/// <param name="From">Первый учётный месяц периода: <c>2026-09</c>.</param>
/// <param name="To">Последний учётный месяц периода.</param>
/// <param name="Months">Месяцы периода так, как их называет реестр («09.2026»): из них ссылка отчёта
/// собирает отбор «Учётный период — один из».</param>
/// <param name="Sites">Стройки — на экране всех строек.</param>
/// <param name="Articles">Статьи вне строек — отдельной группой (ТЗ COST-10.1).</param>
/// <param name="Unallocated">Деньги оплаченных счетов, не лёгшие ни на один объект.</param>
/// <param name="Suppliers">Контрагенты — на экране стройки; строка без идентификатора — «поставщик не указан».</param>
/// <param name="Unmatched">Из затрат — счета со строками без позиции номенклатуры: в затраты вошли.</param>
/// <param name="Payable">Не оплачено и не отклонено — НЕ затраты и от периода не зависит.</param>
/// <param name="VatUnknown">Под «без НДС» — деньги, из которых НДС вычесть нечем: учтены полной суммой.</param>
public sealed record SiteCostsView(
    CostLine? Site, string From, string To, bool WithVat, IReadOnlyList<string> Months,
    IReadOnlyList<CostLine> Sites, IReadOnlyList<CostLine> Articles, CostFigure? Unallocated,
    IReadOnlyList<CostLine> Suppliers,
    CostFigure Total, CostFigure? Unmatched, CostFigure Payable, CostFigure? VatUnknown);

/// <summary>
/// «Затраты по стройке» (задача G5 этапа 2, issue #1098, ТЗ COST-20).
///
/// <para><b>Тонкий отчёт: только свёрнутые числа.</b> Списков оснований у него нет — каждое число ведёт
/// в «Реестр счетов» с готовым отбором и обязано равняться итогу «Суммы» там (решение владельца
/// 05.10.2026 по ревизии Дизайнера). Поэтому отчёт складывает те же строки денег, что и реестр
/// (<see cref="InvoiceMoney" />), и отбирает счета теми же словами, какими их отберёт ссылка:
/// оплаченные и не отклонённые, с деньгами в учётных месяцах периода.</para>
///
/// <para><b>Только счета.</b> Расходных накладных в модуле ещё нет (D1, issue #1083); места под них
/// отчёт не рисует — ноль читался бы как «по накладным затрат нет».</para>
///
/// <para><b>Право — <c>costs.report.read</c></b>: суммы по всем стройкам экземпляра. Оно включает
/// чтение счетов, так что по ссылке отчёта человек в реестр попадёт.</para>
/// </summary>
public static class SiteCostsEndpoints
{
    public const string Lost = "стройка удалена";
    public const string NoSupplier = "поставщик не указан";

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/costs/site-costs", ReadAsync)
            .WithTags("Затраты по стройке")
            .RequireAuthorization(AppPolicies.Permission("costs.report.read"));

    /// <param name="site">Стройка; не названа — все стройки.</param>
    /// <param name="from">Первый учётный месяц, <c>2026-09</c>; не назван — текущий месяц компании.</param>
    /// <param name="to">Последний учётный месяц; не назван — тот же, что первый.</param>
    /// <param name="vat"><c>without</c> — без НДС; иначе суммы как в бумаге.</param>
    private static async Task<Ok<SiteCostsView>> ReadAsync(
        Guid? site, string? from, string? to, string? vat,
        CostsDbContext db, AllocationPlacesSource places, IModuleCatalog catalog, IModuleClock clock, CancellationToken ct)
    {
        var today = await clock.TodayAsync(ct);
        var first = Month(from, "from") ?? PaymentPosting.MonthOf(today);
        var last = Month(to, "to") ?? first;
        if (last < first)
            throw new InvalidRequestException($"Период задан наоборот: «по» ({last:MM.yyyy}) раньше, чем «с» ({first:MM.yyyy}).");
        var through = last.AddMonths(1).AddDays(-1);
        var withVat = vat != "without";

        var known = await places.LoadAsync(ct);
        if (site is { } asked && known.Sites.All(s => s.Id != asked))
            throw new NotFoundException("Такой стройки нет: её удалили либо ссылка устарела.");

        // Оплаченные неотклонённые счета, у которых в период вошла хоть часть денег; у стройки — её доля.
        // Те же слова, какими счета отберёт ссылка в реестр, — иначе число отчёта и итог реестра разойдутся.
        var paid = db.Invoices.AsNoTracking().Where(i =>
            i.Payment == InvoicePaymentState.Paid && i.State != InvoiceState.Rejected
            && (db.InvoiceAllocations.Any(a => a.InvoiceId == i.Id && a.AccountingOn >= first && a.AccountingOn <= through
                                               && (site == null || a.ConstructionId == site))
                || (site == null && i.RemainderAccountingOn >= first && i.RemainderAccountingOn <= through)));
        var unpaid = db.Invoices.AsNoTracking().Where(i =>
            i.Payment != InvoicePaymentState.Paid && i.State != InvoiceState.Rejected
            && (site == null || db.InvoiceAllocations.Any(a => a.InvoiceId == i.Id && a.ConstructionId == site)));

        var (costs, lines) = await InvoicesAsync(db, paid, ct);
        var (waiting, waitingLines) = await InvoicesAsync(db, unpaid, ct);

        var result = SiteCosts.Of(costs, lines, first, through, site, withVat);
        var payable = SiteCosts.Payable(waiting, waitingLines, site, withVat);

        var suppliers = (await catalog.ListAsync(CostsRecordTypes.OrganizationCode, ct))
            ?.ToDictionary(o => o.Id, o => o.DisplayName) ?? [];
        var byName = StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true);
        CostLine Line(Guid? id, string name, CostFigure figure) => new(id, name, figure.Invoices, figure.Amount);

        return TypedResults.Ok(new SiteCostsView(
            site is { } chosen ? new(chosen, known.Sites.First(s => s.Id == chosen).Name, result.Total.Invoices, result.Total.Amount) : null,
            $"{first:yyyy-MM}", $"{last:yyyy-MM}", withVat,
            [.. Months(first, last).Select(PaymentViews.Month)],
            [.. result.Sites.Select(s => Line(s.Key, known.Sites.FirstOrDefault(k => k.Id == s.Key)?.Name ?? Lost, s.Value))
                .OrderBy(l => l.Name, byName)],
            [.. result.Articles.Select(a => Line(a.Key, known.Articles.FirstOrDefault(k => k.Id == a.Key)?.Name ?? InvoiceShares.Lost, a.Value))
                .OrderBy(l => l.Name, byName)],
            result.Unallocated,
            [.. result.Suppliers
                .Select(s => Line(s.Supplier, s.Supplier is { } id && suppliers.TryGetValue(id, out var name) ? name : NoSupplier, s.Figure))
                // «Поставщик не указан» — последним: это не название, а его отсутствие.
                .OrderBy(l => l.Id is null).ThenBy(l => l.Name, byName)],
            result.Total, result.Unmatched, payable, result.VatUnknown));
    }

    /// <summary>Счета отчёта с их деньгами — тем же читателем, что у реестра, — и НДС их строк.</summary>
    private static async Task<(IReadOnlyList<CostInvoice>, IReadOnlyDictionary<Guid, LineVat>)> InvoicesAsync(
        CostsDbContext db, IQueryable<Invoice> invoices, CancellationToken ct)
    {
        var heads = await invoices.Select(i => new { i.Id, i.SupplierId, i.Total, i.VatTotal, i.RemainderAccountingOn }).ToListAsync(ct);
        var owners = invoices.Select(i => i.Id);
        var money = await InvoiceMoney.ReadAsync(db,
            [.. heads.Select(h => new InvoiceHead(h.Id, h.Total, h.RemainderAccountingOn))], owners, null, ct);
        var lines = await db.InvoiceLines.AsNoTracking().Where(l => owners.Contains(l.InvoiceId))
            .Select(l => new { l.Id, l.InvoiceId, l.Amount, l.VatAmount, l.NomenclatureId }).ToListAsync(ct);
        var unmatched = lines.Where(l => l.NomenclatureId is null).Select(l => l.InvoiceId).ToHashSet();

        return (
            [.. heads.Select(h => new CostInvoice(h.Id, h.SupplierId, h.Total, h.VatTotal, unmatched.Contains(h.Id),
                money.GetValueOrDefault(h.Id) ?? []))],
            lines.ToDictionary(l => l.Id, l => new LineVat(l.Amount, l.VatAmount)));
    }

    private static IEnumerable<DateOnly> Months(DateOnly first, DateOnly last)
    {
        for (var month = first; month <= last; month = month.AddMonths(1)) yield return month;
    }

    /// <summary>Месяц из адреса: <c>2026-09</c>. Не названный — null; названный негодно — отказ, а не «текущий».</summary>
    private static DateOnly? Month(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? null
        : DateOnly.TryParseExact($"{value}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month) ? month
        : throw new InvalidRequestException($"Месяц «{name}» назван не так: «{value}». Ждём год и месяц — например, 2026-09.");
}
