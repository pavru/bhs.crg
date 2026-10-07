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
/// <param name="Lost">Деньги на удалённых объектах — одной строкой, названной так же, как их называет
/// реестр.</param>
/// <param name="Unallocated">Деньги оплаченных счетов, не лёгшие ни на один объект.</param>
/// <param name="Suppliers">Контрагенты — на экране стройки; строки без идентификатора — «поставщик не
/// указан» и, без ссылки в реестр, «поставщик удалён».</param>
/// <param name="Sections">Разделы стройки — на её экране, второй срез той же суммы; «без раздела» и
/// «раздел удалён» — строками без идентификатора. Разделы, названные одинаково, — одной строкой.</param>
/// <param name="Unmatched">Из затрат — счета со строками без позиции номенклатуры: в затраты вошли.</param>
/// <param name="Payable">Не оплачено и не отклонено — НЕ затраты и от периода не зависит.</param>
/// <param name="VatUnknown">Под «без НДС» — деньги, из которых НДС вычесть нечем: учтены полной суммой.</param>
/// <param name="PayableVatUnknown">То же — в «к оплате».</param>
public sealed record SiteCostsView(
    CostLine? Site, string From, string To, bool WithVat, IReadOnlyList<string> Months,
    IReadOnlyList<CostLine> Sites, IReadOnlyList<CostLine> Articles, CostLine? Lost, CostFigure? Unallocated,
    IReadOnlyList<CostLine> Suppliers, IReadOnlyList<CostLine> Sections,
    CostFigure Total, CostFigure? Unmatched, CostFigure Payable, CostFigure? VatUnknown, CostFigure? PayableVatUnknown);

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
    public const string NoSupplier = "поставщик не указан";

    /// <summary>Организации счёта больше нет в справочнике. Без ссылки: реестр такую не называет никак.</summary>
    public const string LostSupplier = "поставщик удалён";

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
        CostsDbContext db, AllocationPlacesSource places, IModuleCatalog catalog, IModuleClock clock,
        IModuleSettings settings, CancellationToken ct)
    {
        var tolerance = await settings.GetAsync(CostsSettings.AllocationTolerance, ct);
        var today = await clock.TodayAsync(ct);
        var first = Month(from, "from") ?? PaymentPosting.MonthOf(today);
        var last = Month(to, "to") ?? first;
        if (last < first)
            throw new InvalidRequestException($"Период задан наоборот: «по» ({last:MM.yyyy}) раньше, чем «с» ({first:MM.yyyy}).");
        // Период — в пределах календаря реестра: месяц вне него реестр называет «период не определён»,
        // и ссылка отчёта под таким месяцем не нашла бы ничего. Заодно это граница размера ответа —
        // месяцы периода уходят в отбор ссылки списком (ревью PR #1200).
        foreach (var month in new[] { first, last })
            if (!InvoicePeriods.Covers(month, today))
                throw new InvalidRequestException(
                    $"Учётного месяца {month:MM.yyyy} в календаре нет: отчёт строится с {InvoicePeriods.First:MM.yyyy} по {InvoicePeriods.Last(today):MM.yyyy}.");
        var through = last.AddMonths(1).AddDays(-1);
        var withVat = vat != "without";

        var known = await places.LoadAsync(ct);
        if (site is { } asked && known.Sites.All(s => s.Id != asked))
            throw new NotFoundException("Такой стройки нет: её удалили либо ссылка устарела.");

        var paid = Paid(db, first, through, site);
        var unpaid = db.Invoices.AsNoTracking().Where(i =>
            i.Payment != InvoicePaymentState.Paid && i.State != InvoiceState.Rejected
            && (site == null || db.InvoiceAllocations.Any(a => a.InvoiceId == i.Id && a.ConstructionId == site)));

        // Названия объектов и разделов — те же, какими реестр сверяет отбор (InvoiceShares): ссылка
        // строки несёт название, и назови отчёт объект по-своему, реестр под ней не нашёл бы ничего.
        var shares = InvoiceShares.Of(known);
        var labels = shares.Labels;

        var (costs, lines) = await InvoicesAsync(db, paid, tolerance, ct);
        // «К оплате» с НДС по всем стройкам — суммы к оплате из самих записей: строки и части ВСЕХ
        // неоплаченных счетов ради них не читаются.
        var (waiting, waitingLines) = site is null && withVat
            ? ([.. (await unpaid.Select(i => new { i.Id, i.SupplierId, i.Total, i.VatTotal }).ToListAsync(ct))
                    .Select(i => new CostInvoice(i.Id, i.SupplierId, i.Total, i.VatTotal, false, false, []))],
                new Dictionary<Guid, LineVat>())
            : await InvoicesAsync(db, unpaid, tolerance, ct);

        // Доля в срезе стройки — всегда на стройку, раздел у неё есть (хотя бы «без раздела»).
        var result = SiteCosts.Of(costs, lines, first, through, site, withVat, labels.Keys.ToHashSet(), part => shares.SectionOf(part)!);
        var (payable, payableVatUnknown) = SiteCosts.Payable(waiting, waitingLines, site, withVat);

        // Контрагенты — только на экране стройки: без неё справочник не читаем.
        var suppliers = site is null
            ? []
            : (await catalog.ListAsync(CostsRecordTypes.OrganizationCode, RecordsFor.Display, ct))?.ToDictionary(o => o.Id, o => o.DisplayName) ?? [];
        var byName = InvoiceShares.ByName;
        CostLine Line(Guid? id, string name, CostFigure figure, bool linked = true) =>
            new(id, name, figure.Invoices, figure.Amount, linked);
        CostFigure Sum(IEnumerable<CostFigure> figures) => figures.Aggregate(new CostFigure(0, 0), (a, b) => new(a.Invoices + b.Invoices, a.Amount + b.Amount));
        var lostSuppliers = result.Suppliers.Where(s => s.Supplier is { } id && !suppliers.ContainsKey(id)).ToList();

        return TypedResults.Ok(new SiteCostsView(
            site is { } chosen ? new(chosen, labels[chosen], result.Total.Invoices, result.Total.Amount) : null,
            $"{first:yyyy-MM}", $"{last:yyyy-MM}", withVat,
            [.. Months(first, last).Select(PaymentViews.Month)],
            [.. result.Sites.Select(s => Line(s.Key, labels[s.Key], s.Value)).OrderBy(l => l.Name, byName)],
            [.. result.Articles.Select(a => Line(a.Key, labels[a.Key], a.Value)).OrderBy(l => l.Name, byName)],
            result.Lost is { } lost ? Line(null, InvoiceShares.Lost, lost) : null,
            result.Unallocated,
            [.. result.Suppliers.Except(lostSuppliers)
                .Select(s => Line(s.Supplier, s.Supplier is { } id ? suppliers[id] : NoSupplier, s.Figure))
                // Удалённые — одной строкой и без ссылки: реестр их не называет, отбора под них нет. Число
                // счетов складывается: поставщик у счёта один.
                .Concat(lostSuppliers.Count == 0 ? [] : [Line(null, LostSupplier, Sum(lostSuppliers.Select(s => s.Figure)), linked: false)])
                // Строки без названия — последними: это не название, а его отсутствие.
                .OrderBy(l => l.Id is null).ThenBy(l => l.Name, byName)],
            // Разделы — на экране стройки. В отчёте раздел назван коротко («4 эт.»): стройка стоит в
            // заголовке; реестр зовёт его вместе со стройкой, и это название строка несёт для ссылки.
            [.. result.Sections
                .Select(s => new CostLine(s.Section.Id, s.Section.Short, s.Figure.Invoices, s.Figure.Amount, Registry: s.Section.Registry))
                .OrderBy(l => l.Id is null).ThenBy(l => l.Name, byName)],
            result.Total, result.Unmatched, payable, result.VatUnknown, payableVatUnknown));
    }

    /// <summary>
    /// Оплаченные счета, у которых в дни периода вошла хоть одна учётная дата; у стройки — дата её доли.
    /// Неотклонённые — те же слова, какими счета отберёт ссылка в реестр: иначе число отчёта и итог
    /// реестра разойдутся.
    /// </summary>
    /// <param name="rejectedToo">И отклонённые: в затраты они не входят, но закрытый период запирает и
    /// их (см. <see cref="ClosedPeriodGuard" />) — так считает диалог закрытия.</param>
    internal static IQueryable<Invoice> Paid(
        CostsDbContext db, DateOnly first, DateOnly through, Guid? site, bool rejectedToo = false) =>
        db.Invoices.AsNoTracking().Where(i =>
            i.Payment == InvoicePaymentState.Paid && (rejectedToo || i.State != InvoiceState.Rejected)
            && (db.InvoiceAllocations.Any(a => a.InvoiceId == i.Id && a.AccountingOn >= first && a.AccountingOn <= through
                                               && (site == null || a.ConstructionId == site))
                || (site == null && i.RemainderAccountingOn >= first && i.RemainderAccountingOn <= through)));

    /// <summary>Счета отчёта с их деньгами — тем же читателем, что у реестра, — и НДС их строк.</summary>
    internal static async Task<(IReadOnlyList<CostInvoice>, IReadOnlyDictionary<Guid, LineVat>)> InvoicesAsync(
        CostsDbContext db, IQueryable<Invoice> invoices, decimal tolerance, CancellationToken ct)
    {
        var heads = await invoices.Select(i => new { i.Id, i.SupplierId, i.Total, i.VatTotal, i.RemainderAccountingOn, i.Payment, i.State }).ToListAsync(ct);
        var owners = invoices.Select(i => i.Id);
        var money = await InvoiceMoney.ReadAsync(db,
            [.. heads.Select(h => new InvoiceHead(h.Id, h.Total, h.RemainderAccountingOn, h.Payment == InvoicePaymentState.Paid))], owners, null, tolerance, ct);
        var lines = await db.InvoiceLines.AsNoTracking().Where(l => owners.Contains(l.InvoiceId))
            .Select(l => new { l.Id, l.InvoiceId, l.Amount, l.VatAmount, l.NomenclatureId }).ToListAsync(ct);
        var unmatched = lines.Where(l => l.NomenclatureId is null).Select(l => l.InvoiceId).ToHashSet();
        var vatByLines = lines.Where(l => l.VatAmount is not null).Select(l => l.InvoiceId).ToHashSet();

        return (
            [.. heads.Select(h => new CostInvoice(h.Id, h.SupplierId, h.Total, h.VatTotal, unmatched.Contains(h.Id), vatByLines.Contains(h.Id),
                money.GetValueOrDefault(h.Id) ?? [], h.State == InvoiceState.Parsed))],
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
