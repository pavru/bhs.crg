using System.Data;
using System.Text.Encodings.Web;
using System.Text.Json;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Costs.Tables;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Раздел «Счета и накладные» в диалоге закрытия периода (задача E1b, issue #1099; ТЗ CORE-35, COST-16).
///
/// <para><b>Своего расчёта денег здесь нет.</b> Счета отбирает тот же запрос, деньги читает тот же
/// читатель и складывает та же функция, что у отчёта «Затраты по стройке»
/// (<see cref="SiteCostsEndpoints.Paid" />, <see cref="SiteCosts.Of" />): «войдут в закрытый период» за
/// целые месяцы равно итогу отчёта за них и итогу «Суммы» в реестре. Посчитай диалог сам — человек
/// закрывал бы период по одной цифре, а сверял по другой.</para>
///
/// <para><b>Что считается.</b> Только деньги, чей учётный день закрытие закрывает ВПЕРВЫЕ
/// (<see cref="ModuleClosingScope.ClosesAnew" />): при закрытии компании доля на стройку, закрытую своим
/// закрытием дальше, уже заперта и второй раз не называется (ревью PR #1201). Поэтому равенство с отчётом
/// «Затраты по стройке» — за дни, где таких строек нет. Неоплаченных счетов здесь нет: они не
/// принадлежат ни одному периоду (ТЗ COST-16).</para>
///
/// <para><b>Деньги — взглядом «на момент закрытия»</b> (E2, issue #1100, spec v3.3 п. 1.8, I6): запись
/// закрытия обязана хранить то, что этот месяц покажет, пока закрыт. Поэтому денежных строк две:
/// «вошедшие в период» — счета БЕЗ книги исправлений, их деньги основной таблицы; «счета с книгой
/// исправлений» — строки книги в этих днях, и открытия, и исправления своим днём, и поправки днём
/// исправления. Посчитай диалог второе по основной таблице — повторное закрытие месяца, исправленного
/// после первого, записало бы уже исправленное число, а книга держит в нём прежнее.</para>
///
/// <para>⚠️ Замка записи отчёт НЕ берёт и через <c>InvoiceDesk</c> не ходит: при закрытии его зовут под
/// исключительным замком ядра (см. <see cref="IModuleClosingReport" />).</para>
/// </summary>
public sealed class CostsClosingReport(
    CostsDbContext db, AllocationPlacesSource places, IModuleClock clock, IModuleSettings settings)
    : IModuleClosingReport
{
    /// <summary>Экран «Реестр счетов». Тот же адрес, что у ссылок отчёта «Затраты по стройке» на клиенте
    /// (<c>features/costs/siteCosts.ts</c>, <c>REGISTRY</c>).</summary>
    public const string Registry = "/tables/costs.invoices/registry";

    // Накладные в перечне есть (issue #1285) — строками <see cref="WaybillClosingLines" />.
    public const string DateRule =
        "Счёт относится к периоду по учётному периоду оплаты. Расходная накладная — по учётной дате, которую " +
        "получает при проведении; черновик накладной к периоду не относится.";

    /// <summary>Суммы счетов открывает право на отчёты по затратам — то же, что у «Затрат по стройке».</summary>
    private const string Amounts = "costs.report.read";

    private static readonly ModuleClosingUnit Invoices = new("счёт", "счёта", "счетов");

    public string Module => "costs";

    public async Task<ModuleClosingSection> ReportAsync(ModuleClosingScope scope, CancellationToken ct = default)
    {
        var first = scope.From ?? DateOnly.MinValue;
        var site = scope.ConstructionId;

        // Один снимок на все чтения — как у отчёта (ревью PR #1372): признак «есть книга» и строки книги
        // читаются разными запросами. При самом закрытии исправления ждут замка периода, а предпросмотр
        // его не держит — расхождение там поймала бы отметка, но лучше его не рождать.
        await using var snapshot = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct)
            : null;
        var (read, lines) = await SiteCostsEndpoints.InvoicesAsync(db, SiteCostsEndpoints.Paid(db, first, scope.Through, site),
            await settings.GetAsync(CostsSettings.AllocationTolerance, ct), ct);
        // У каждого счёта оставлены только деньги впервые закрываемых дней — дальше всё считается по ним.
        IReadOnlyList<CostInvoice> invoices = [.. read.Select(i => i with
        {
            Money = [.. i.Money.Where(m => m.AccountingOn is { } day && scope.ClosesAnew(m.Part?.ConstructionId, day))],
        })];
        CostFigure Money(IReadOnlyList<CostMoney> of) => SiteCosts.Of(of, first, scope.Through, site, withVat: true).Total;

        // Не завершено: счёт не разобран либо у него остался неразнесённый остаток. Остаток — только
        // положительный: отрицательный — копеечная поправка к деньгам долей (строки чуть больше суммы к
        // оплате), а не «не разнесено» (решение владельца 05.10.2026). И только остаток ЭТИХ дней: счёт
        // с долей в закрываемом месяце и остатком, перенесённым в следующий, здесь завершён. У стройки
        // остатка нет: он лежит на компании. Готовность — свойство документа, а не книги: деньги этой
        // строки — основной таблицы.
        var unsettled = Money(SiteCosts.Corrected(invoices.Where(i =>
            !i.Parsed || (site is null && i.Money.Any(m => m.Part is null && m.Amount > 0))), lines));

        // Вошедшие в период — счета без книги: их деньги во впервые закрываемых днях лежат в основной
        // таблице, и закрытый месяц покажет ровно их. Счета с книгой — её строками этих дней (spec п. 1.8).
        // Только деньги этой стройки (у стройки) — иначе счёт с деньгами на другой стройке выпал бы из
        // «запрутся» как будто «вошедший» (ревью PR #1372).
        var anew = SiteCosts.Corrected(invoices, lines)
            .Where(m => site is null || m.Grain.Cell.Target?.ConstructionId == site).ToList();
        var booked = read.Where(i => i.Booked).Select(i => i.Id).ToHashSet();
        var plain = anew.Where(m => !booked.Contains(m.Invoice)).ToList();
        var book = (await CostViews.BookAsync(db, first, scope.Through, site, ct))
            .Where(m => scope.ClosesAnew(m.Grain.Cell.Target?.ConstructionId, m.Grain.Day)).ToList();
        var entering = Money(plain);
        var corrections = Money(book);
        var locked = await LockedAsync(scope, invoices, first, ct);

        List<ModuleClosingLine> unfinished = [], frozen = [];
        if (unsettled.Invoices > 0)
            unfinished.Add(new("unsettled",
                site is null ? "Оплачены в периоде, но не разнесены или не разобраны" : "Оплачены в периоде, но не разобраны",
                unsettled.Invoices, Invoices, unsettled.Amount, Amounts,
                // Сумма — не «сколько не разнесено», а деньги этих счетов: у неразобранного счёта
                // ненадёжна вся разноска, и делить строку на два вида сумм значило бы объяснять её дольше,
                // чем читать.
                "Сумма — деньги этих счетов в периоде. После закрытия их разноска по стройкам останется как есть."));
        if (entering.Invoices > 0)
            frozen.Add(new("entering", "Оплаченные счета, вошедшие в период", entering.Invoices, Invoices, entering.Amount, Amounts,
                "Их оплату, учётный период и разноску изменить будет нельзя. Неоплаченные счета к периоду не относятся и остаются открытыми.",
                // ⚠️ Реестр показывает документы, а не книгу: счёт с книгой, чьи деньги лежат в этих днях,
                // реестр под ссылкой перечислит, а строка — нет (spec п. 1.8).
                anew.Any(m => booked.Contains(m.Invoice)) ? null : await RegistryLinkAsync(scope, entering.Invoices, ct)));
        if (corrections.Invoices > 0)
            frozen.Add(new("corrected", "Счета с книгой исправлений", corrections.Invoices, Invoices, corrections.Amount, Amounts,
                // Без ссылки: реестр показывает счета как они есть сейчас, а не их книгу.
                "Деньги их книги исправлений в этих днях — так их покажет закрытый период. Сумма бывает нулевой " +
                "и отрицательной: исправление, сделанное после закрытия, ложится днём исправления."));
        // «Запрутся» — отдельной строкой, если запрутся и счета, которых в денежных строках нет.
        if (locked.Except(plain.Select(m => m.Invoice)).Except(book.Select(m => m.Invoice)).Any())
            frozen.Add(new("locked", "Запрутся целиком", locked.Count, Invoices,
                Note: "Вместе со счетами, у которых в периоде есть доля без денег: " +
                      "счёт запирается целиком, если закрыта хоть одна его учётная дата."));

        // Накладные — после счетов: деньги периода человек сверяет первыми.
        var waybills = await WaybillClosingLines.OfAsync(db, scope, ct);
        return new(DateRule, [.. unfinished, .. waybills.Unfinished], [.. frozen, .. waybills.Frozen]);
    }

    /// <summary>
    /// Ссылка строки «вошедшие в период» в реестр — тем же отбором, что у итога отчёта «Затраты по
    /// стройке»: учётные месяцы, «не отклонён», у стройки — её объект. Под ним число счетов и итог «Суммы»
    /// реестра равны строке (сторож — тест, сверяющий строку с реестром под этой самой ссылкой).
    ///
    /// <para>⚠️ Только когда закрываемые впервые дни — ЦЕЛЫЕ учётные месяцы (решение владельца
    /// 05.10.2026): реестр отбирает по месяцу, и за неполный показал бы больше, чем строка. Поэтому
    /// ссылки нет у первого закрытия контура (закрывается всё, что было раньше, — начала нет), у периода
    /// не с первого по последнее число, при стройке, закрытой дальше компании (её доли реестр покажет, а
    /// строка не считает), у стройки без названия или с названием, которое носит ещё один объект (отбор
    /// реестра идёт по названию), и когда реестр под этим отбором перечислит не столько счетов, сколько в
    /// строке (см. <see cref="ListedAsync" />). «Не завершено» ссылки не имеет: точного отбора
    /// «не разобран ИЛИ с остатком этих дней» у реестра нет.</para>
    /// </summary>
    private async Task<string?> RegistryLinkAsync(ModuleClosingScope scope, int counted, CancellationToken ct)
    {
        if (scope.From is not { Day: 1 } from || scope.Through != PaymentPosting.MonthOf(scope.Through).AddMonths(1).AddDays(-1)
            || scope.ClosedAhead.Count > 0)
            return null;

        var today = await clock.TodayAsync(ct);
        var months = new List<DateOnly>();
        for (var month = from; month <= scope.Through; month = month.AddMonths(1)) months.Add(month);
        if (!months.All(m => InvoicePeriods.Covers(m, today))) return null;
        if (await ListedAsync(scope, from, ct) != counted) return null;

        var conditions = new List<object>();
        if (scope.ConstructionId is { } site)
        {
            // Отбор реестра идёт по НАЗВАНИЮ объекта: без названия стройки ссылку собрать нечем, а под
            // названием, которое носит ещё и другая стройка или статья, реестр покажет счета обеих.
            var known = await places.LoadAsync(ct);
            if (known.Sites.FirstOrDefault(s => s.Id == site) is not { } named) return null;
            if (known.Sites.Count(s => s.Name == named.Name) + known.Articles.Count(a => a.Name == named.Name) != 1) return null;
            conditions.Add(new { type = "condition", column = InvoiceTable.ObjectsKey, op = "eq", value = named.Name });
        }
        conditions.Add(new { type = "condition", column = InvoiceTable.PeriodKey, op = "in", values = months.Select(PaymentViews.Month) });
        conditions.Add(new
        {
            type = "condition", column = InvoiceRequisites.StateKey, op = "neq", value = InvoiceRequisites.Label(InvoiceState.Rejected),
        });

        var filter = JsonSerializer.Serialize(new { type = "group", logic = "and", children = conditions }, LinkJson);
        return $"{Registry}#filter={Uri.EscapeDataString(filter)}";
    }

    /// <summary>
    /// Сколько счетов реестр перечислит под отбором ссылки. Он отбирает счёт двумя НЕЗАВИСИМЫМИ
    /// условиями — «есть доля на объект» и «есть учётная дата в этих месяцах», — а строка считает только
    /// деньги этого объекта в эти дни. Расходятся они на счёте, у которого в месяцах есть дата, но не
    /// деньги строки: доля без цены либо, у стройки, доля ДРУГОЙ стройки (своя легла в другой месяц —
    /// ревью PR #1202). Сумма под ссылкой сошлась бы, число счетов — нет.
    /// </summary>
    private Task<int> ListedAsync(ModuleClosingScope scope, DateOnly from, CancellationToken ct)
    {
        var dated = SiteCostsEndpoints.Paid(db, from, scope.Through, site: null);
        return scope.ConstructionId is { } site
            ? dated.CountAsync(i => db.InvoiceAllocations.Any(a => a.InvoiceId == i.Id && a.ConstructionId == site), ct)
            : dated.CountAsync(ct);
    }

    private static readonly JsonSerializerOptions LinkJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Какие счета запрутся. Запирается счёт целиком и по ЛЮБОЙ закрытой учётной дате — и с долей без
    /// денег, которой в затратах периода нет.
    ///
    /// <para>⚠️ <b>День остатка — по обоим правилам сразу.</b> Деньги «вошедших» видят остаток там, где его
    /// ЧИТАЮТ (<see cref="PaymentPosting.Money" />): у счёта без штампа дня остатка (данные до E2) — в дне
    /// самой поздней доли (ревизия Архитектора, разд. 6). А запирает счёт <see cref="ClosedPeriodGuard.LockOf" />
    /// по СЫРОЙ колонке — и колонку, оставшуюся у нулевого остатка («фантом», spec v3.3 разд. 3), он тоже
    /// видит (ревью PR #1372). Возьми одно правило — строка разошлась бы либо с деньгами диалога, либо с
    /// тем, что запрётся на деле. При соблюдённом инварианте колонки правила совпадают.</para>
    /// </summary>
    /// <param name="invoices">Оплаченные счета отрезка — с деньгами только впервые закрываемых дней.</param>
    private async Task<HashSet<Guid>> LockedAsync(
        ModuleClosingScope scope, IReadOnlyList<CostInvoice> invoices, DateOnly first, CancellationToken ct)
    {
        var site = scope.ConstructionId;
        HashSet<Guid> locked = [.. invoices.Where(i => i.Money.Any(m => site is null || m.Part?.ConstructionId == site)).Select(i => i.Id)];
        if (site is not null) return locked;

        var stamped = await SiteCostsEndpoints.Paid(db, first, scope.Through, site: null)
            .Where(i => i.RemainderAccountingOn >= first && i.RemainderAccountingOn <= scope.Through)
            .Select(i => new { i.Id, Day = i.RemainderAccountingOn!.Value }).ToListAsync(ct);
        locked.UnionWith(stamped.Where(r => scope.ClosesAnew(null, r.Day)).Select(r => r.Id));
        return locked;
    }
}
