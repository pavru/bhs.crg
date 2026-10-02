using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Endpoints;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Tables;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Tables;

/// <summary>
/// Таблица «Счета на оплату» — зерно «счёт» (ТЗ CORE-33, COST-20.1; задача G1b, issue #1089).
///
/// <para><b>Таблица открыта модулем, суммы — правом на счета</b> (решение владельца 01.10.2026).
/// Ключ таблицы — код модуля: её видит всякий, кому открыт «Счета и накладные», в том числе с одним
/// правом на накладные. Деньги — <c>costs.invoice.read</c>: право на накладные не даёт права на счета
/// (ТЗ COST-29). Колонка суммы у того, кому её не положено, приходит С ПРИЧИНОЙ «нет права на суммы»,
/// а не исчезает.</para>
///
/// <para>⚠️ Это значит, что номер, поставщик и назначение счёта видны и без права на счета — решение
/// принято вслух, и закрывается оно здесь же: колонке достаточно назвать право.</para>
///
/// <para><b>Отбор по объекту меняет смысл суммы, и таблица это говорит</b> (ТЗ CORE-33, COST-20.1;
/// задача G1c, issue #1090). «Объект» — перечень строек и статей, на которые разнесён счёт; условие
/// по нему — «есть часть на этот объект». Под таким отбором «Сумма» показывает ДОЛЮ счёта по разноске
/// на названные объекты и приходит с подписью «доля: …»; полная сумма остаётся в «Сумма к оплате».
/// Без отбора по объекту обе суммы равны.</para>
///
/// <para><b>«Осталось дней» и «Просрочен» считаются от сегодня и не хранятся</b> (ТЗ COST-9.1, CORE-33;
/// задача G1c, issue #1090): источник считает их на чтении от срока «оплатить до» в поясе компании,
/// и по ним работают отбор и сортировка — правило одно на запрос и на клетку (<see cref="InvoiceDue" />).</para>
/// </summary>
public static class InvoiceTable
{
    public const string Code = "invoices";

    /// <summary>
    /// Объекты разноски. Ключи двух колонок, за которыми НЕ стоит поле типа, — нарочно не похожие на
    /// ключ поля, которое заказчик допишет в тип: «Объект» и «Сумма» он допишет скорее всего, и поле с
    /// тем же ключом молча спряталось бы за системной колонкой.
    /// </summary>
    public const string ObjectsKey = "ОбъектыРазноски";

    /// <summary>Сумма по отбору: вся сумма счёта либо его доля на названные отбором объекты.</summary>
    public const string AmountKey = "СуммаПоОтбору";

    /// <summary>Сколько дней до срока оплаты; у просроченного счёта — отрицательное.</summary>
    public const string DaysLeftKey = "ДнейДоСрока";

    /// <summary>Срок оплаты прошёл, а счёт оплаты ещё ждёт.</summary>
    public const string OverdueKey = "СрокПросрочен";

    private const string Amounts = "суммы";

    public static ModuleTable Declaration { get; } = new(
        Code,
        "Счета на оплату",
        "счёт",
        Requires: "costs",
        ModuleTableIsolation.None,
        "Отдаёт все счета экземпляра — всем, кому открыт модуль «Счета и накладные»; суммы — только с " +
        "правом «видеть счета на оплату»",
        [
            new(InvoiceRequisites.NumberKey, "Номер счёта", ModuleTableColumnKind.Text),
            new(InvoiceRequisites.DateKey, "Дата счёта", ModuleTableColumnKind.Date),
            new(InvoiceRequisites.SupplierKey, "Поставщик", ModuleTableColumnKind.Text),
            new(InvoiceRequisites.PayerKey, "Плательщик", ModuleTableColumnKind.Text),
            new(InvoiceRequisites.PurposeKey, "Назначение", ModuleTableColumnKind.Text),
            new(ObjectsKey, "Объект", ModuleTableColumnKind.List),
            new(AmountKey, "Сумма", ModuleTableColumnKind.Number, "costs.invoice.read", Amounts,
                DependsOnFilter: true),
            new(InvoiceRequisites.TotalKey, "Сумма к оплате", ModuleTableColumnKind.Number,
                "costs.invoice.read", Amounts),
            new(InvoiceRequisites.VatTotalKey, "В том числе НДС", ModuleTableColumnKind.Number,
                "costs.invoice.read", Amounts),
            new(InvoiceRequisites.ShippedOnKey, "Дата отгрузки", ModuleTableColumnKind.Date),
            new(InvoiceRequisites.DeferralKey, "Отсрочка, дней", ModuleTableColumnKind.Number),
            new(InvoiceRequisites.DueDateKey, "Оплатить до", ModuleTableColumnKind.Date),
            new(DaysLeftKey, "Осталось дней", ModuleTableColumnKind.Number),
            new(OverdueKey, "Просрочен", ModuleTableColumnKind.Boolean),
            new(InvoiceRequisites.StateKey, "Состояние документа", ModuleTableColumnKind.Text),
            new(InvoiceRequisites.PaymentKey, "Состояние оплаты", ModuleTableColumnKind.Text),
        ],
        typeof(InvoiceTableRows),
        CostsRecordTypes.InvoiceCode);
}

/// <summary>
/// Строки таблицы счетов. Поля, которые заказчик дописал в тип, лежат в <see cref="Invoice.Data" /> и
/// приходят теми же ключами — их колонки ядро берёт из схемы типа.
/// </summary>
public sealed class InvoiceTableRows(
    CostsDbContext db, IModuleCatalog catalog, AllocationPlacesSource places, IModuleClock clock)
    : IModuleTableRows
{
    private static readonly IReadOnlyDictionary<InvoiceState, string> States =
        Enum.GetValues<InvoiceState>().ToDictionary(s => s, InvoiceRequisites.Label);

    private static readonly IReadOnlyDictionary<InvoicePaymentState, string> Payments =
        Enum.GetValues<InvoicePaymentState>().ToDictionary(p => p, InvoiceRequisites.Label);

    public async Task<ModuleTablePage> ReadAsync(ModuleTableQuery query, CancellationToken ct)
    {
        // Названия организаций — одним списком: вида «Организация» на чистой установке может не быть
        // вовсе (см. InvoiceEndpoints.SupplierNamesAsync). Нужны и строкам, и отбору по названию.
        var names = (await catalog.ListAsync(CostsRecordTypes.OrganizationCode, ct))
            ?.ToDictionary(o => o.Id, o => o.DisplayName) ?? [];

        // Объекты разноски — стройки и статьи вне строек одним списком названий: цель части — ровно
        // одно из двух. Раздел стройки в перечень не идёт: отбор «по стройке» — по стройке целиком.
        var known = await places.LoadAsync(ct);
        var shares = new InvoiceShares(known.Sites.Select(s => (s.Id, s.Name))
            .Concat(known.Articles.Select(a => (a.Id, a.Name)))
            .ToDictionary(o => o.Id, o => o.Name));

        // «Сегодня» — одно на весь ответ: и отбору, и клеткам. Спроси мы его дважды, запрос на
        // границе суток отобрал бы «просроченные» по вчерашнему дню, а признак показал бы по сегодняшнему.
        var today = await clock.TodayAsync(ct);

        var sql = Sql(names, shares.Labels, today);
        var selected = sql.Where(db.Invoices.AsNoTracking(), query.Filter);

        // Отбор НАЗЫВАЕТ объекты — «Сумма» становится долей счёта на них (ТЗ CORE-33). Иначе это сумма
        // счёта целиком, и считает её запрос, как любую числовую колонку.
        var naming = TableFilters.Naming(query.Filter, InvoiceTable.ObjectsKey);
        var shareTotal = naming.Count > 0 && query.Totals?.ContainsKey(InvoiceTable.AmountKey) == true;
        var shareCells = naming.Count > 0 && query.Columns.Contains(InvoiceTable.AmountKey);

        // Итог и число строк — по всему отбору, ДО страницы (ТЗ CORE-33).
        var count = await selected.CountAsync(ct);
        var totals = new Dictionary<string, TableTotal>(await sql.TotalsAsync(selected,
            shareTotal ? query.Totals!.Where(t => t.Key != InvoiceTable.AmountKey).ToDictionary() : query.Totals,
            ct), StringComparer.Ordinal);

        // Порядок по умолчанию — свежие сверху; он же довершает любую сортировку, иначе строки с
        // равными значениями менялись бы местами от страницы к странице.
        IQueryable<Invoice> page = (sql.OrderBy(selected, query.Sort)?.ThenByDescending(i => i.IssuedOn)
                                    ?? selected.OrderByDescending(i => i.IssuedOn))
            .ThenByDescending(i => i.CreatedAt)
            .ThenBy(i => i.Id);
        if (query.Offset > 0) page = page.Skip(query.Offset);
        if (query.Limit is { } limit) page = page.Take(limit);

        var invoices = await page.ToListAsync(ct);

        // Разноску читаем по счетам СТРАНИЦЫ; по всему отбору — только ради итога доли: он обязан
        // считаться по всему отбору, а посчитать долю запросом нельзя (см. InvoiceShares).
        var ids = invoices.Select(i => i.Id).ToList();
        var scope = shareTotal ? selected : db.Invoices.AsNoTracking().Where(i => ids.Contains(i.Id));
        var parts = shareTotal || shareCells || query.Columns.Contains(InvoiceTable.ObjectsKey)
            ? await db.InvoiceAllocations.AsNoTracking()
                .Where(a => scope.Select(i => i.Id).Contains(a.InvoiceId)).ToListAsync(ct)
            : [];
        var amounts = shareTotal || shareCells
            ? await InvoiceShares.ReadAsync(db, scope, parts, p => naming.Any(c => c.Matches(shares.Label(p))), ct)
            : null;
        if (shareTotal) totals[InvoiceTable.AmountKey] = InvoiceShares.Total(amounts!.Values);

        var objects = shares.Objects(parts);
        return new(
            [.. invoices.Select(i => Row(i, names, query.Columns, objects, amounts, today))], count, totals,
            naming.Count == 0 ? null : new Dictionary<string, string> { [InvoiceTable.AmountKey] = shares.Note(naming) });
    }

    /// <summary>
    /// Где лежит каждая колонка таблицы. Описаны ВСЕ объявленные — это проверяет сам построитель;
    /// остальное — поля, которые заказчик дописал в тип, они лежат в <see cref="Invoice.Data" />.
    /// </summary>
    private TableSql<Invoice> Sql(
        Dictionary<Guid, string> names, IReadOnlyDictionary<Guid, string> objects, DateOnly today) =>
        TableSql<Invoice>.Describe(InvoiceTable.Declaration, sql => sql
            .Text(InvoiceRequisites.NumberKey, i => i.Number)
            .Date(InvoiceRequisites.DateKey, i => i.IssuedOn)
            .Lookup(InvoiceRequisites.SupplierKey, i => i.SupplierId, names)
            .Lookup(InvoiceRequisites.PayerKey, i => i.PayerId, names)
            .Text(InvoiceRequisites.PurposeKey, i => i.Purpose)
            // Условие по дочернему зерну: в базе это EXISTS по частям разноски счёта.
            .List(InvoiceTable.ObjectsKey,
                i => db.InvoiceAllocations.Where(a => a.InvoiceId == i.Id).Select(a => a.ConstructionId ?? a.ArticleId),
                objects, InvoiceShares.Lost)
            // Без отбора по объекту «Сумма» — сумма счёта, и итог по ней считает запрос. Отбирать и
            // сортировать по ней ядро не даёт: колонка объявлена зависящей от отбора.
            .Number(InvoiceTable.AmountKey, i => i.Total)
            .Number(InvoiceRequisites.TotalKey, i => i.Total)
            .Number(InvoiceRequisites.VatTotalKey, i => i.VatTotal)
            .Date(InvoiceRequisites.ShippedOnKey, i => i.ShippedOn)
            .Number(InvoiceRequisites.DeferralKey, i => i.DeferralDays)
            .Date(InvoiceRequisites.DueDateKey, i => i.DueDate)
            .Number(InvoiceTable.DaysLeftKey, InvoiceDue.DaysLeft(today))
            .Flag(InvoiceTable.OverdueKey, InvoiceDue.Overdue(today))
            .Lookup(InvoiceRequisites.StateKey, i => (InvoiceState?)i.State, States)
            .Lookup(InvoiceRequisites.PaymentKey, i => (InvoicePaymentState?)i.Payment, Payments)
            .Fields(key => i => i.Data.RootElement.GetProperty(key).GetString()));

    /// <param name="amounts">Доли счетов на названные отбором объекты; null — отбор объектов не называет,
    /// и «Сумма» — сумма счёта целиком.</param>
    private static IReadOnlyDictionary<string, object?> Row(
        Invoice invoice, Dictionary<Guid, string> names, IReadOnlySet<string> open,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> objects, IReadOnlyDictionary<Guid, decimal?>? amounts,
        DateOnly today)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [InvoiceRequisites.NumberKey] = invoice.Number,
            [InvoiceRequisites.DateKey] = invoice.IssuedOn,
            [InvoiceRequisites.SupplierKey] = Name(invoice.SupplierId, names),
            [InvoiceRequisites.PayerKey] = Name(invoice.PayerId, names),
            [InvoiceRequisites.PurposeKey] = invoice.Purpose,
            [InvoiceTable.ObjectsKey] = objects.GetValueOrDefault(invoice.Id) ?? [],
            [InvoiceRequisites.ShippedOnKey] = invoice.ShippedOn,
            [InvoiceRequisites.DeferralKey] = invoice.DeferralDays is { } days ? (decimal)days : null,
            [InvoiceRequisites.DueDateKey] = invoice.DueDate,
            [InvoiceRequisites.StateKey] = InvoiceRequisites.Label(invoice.State),
            [InvoiceRequisites.PaymentKey] = InvoiceRequisites.Label(invoice.Payment),
        };

        // Деньги и срок — только открытые: закрытое ядро всё равно вычистит, но не считать его дешевле,
        // чем считать и выбрасывать.
        if (open.Contains(InvoiceRequisites.TotalKey)) row[InvoiceRequisites.TotalKey] = invoice.Total;
        if (open.Contains(InvoiceRequisites.VatTotalKey)) row[InvoiceRequisites.VatTotalKey] = invoice.VatTotal;
        if (open.Contains(InvoiceTable.AmountKey))
            row[InvoiceTable.AmountKey] = amounts is null ? invoice.Total : amounts.GetValueOrDefault(invoice.Id);

        if (open.Contains(InvoiceTable.DaysLeftKey)) row[InvoiceTable.DaysLeftKey] = InvoiceDue.DaysLeftOf(invoice, today);
        if (open.Contains(InvoiceTable.OverdueKey)) row[InvoiceTable.OverdueKey] = InvoiceDue.OverdueOf(invoice, today);

        foreach (var field in invoice.Data.RootElement.EnumerateObject())
            if (!row.ContainsKey(field.Name)) row[field.Name] = Scalar(field.Value);

        return row;
    }

    /// <summary>Ссылка на организацию, которой нет в справочнике, — пусто, а не идентификатор.</summary>
    private static string? Name(Guid? id, Dictionary<Guid, string> names) =>
        id is { } value && names.TryGetValue(value, out var name) ? name : null;

    /// <summary>Скаляр поля заказчика; составное в клетку не ложится — его колонки ядро и не объявит.</summary>
    private static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetDecimal(out var number) ? number : value.GetRawText(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
