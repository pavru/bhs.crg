using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
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
/// </summary>
public static class InvoiceTable
{
    public const string Code = "invoices";

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
            new(InvoiceRequisites.TotalKey, "Сумма к оплате", ModuleTableColumnKind.Number,
                "costs.invoice.read", Amounts),
            new(InvoiceRequisites.VatTotalKey, "В том числе НДС", ModuleTableColumnKind.Number,
                "costs.invoice.read", Amounts),
            new(InvoiceRequisites.ShippedOnKey, "Дата отгрузки", ModuleTableColumnKind.Date),
            new(InvoiceRequisites.DeferralKey, "Отсрочка, дней", ModuleTableColumnKind.Number),
            new(InvoiceRequisites.DueDateKey, "Оплатить до", ModuleTableColumnKind.Date),
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
public sealed class InvoiceTableRows(CostsDbContext db, IModuleCatalog catalog) : IModuleTableRows
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

        var sql = Sql(names);
        var selected = sql.Where(db.Invoices.AsNoTracking(), query.Filter);

        // Итог и число строк — по всему отбору, ДО страницы (ТЗ CORE-33).
        var count = await selected.CountAsync(ct);
        var totals = await sql.TotalsAsync(selected, query.Totals, ct);

        // Порядок по умолчанию — свежие сверху; он же довершает любую сортировку, иначе строки с
        // равными значениями менялись бы местами от страницы к странице.
        IQueryable<Invoice> page = (sql.OrderBy(selected, query.Sort)?.ThenByDescending(i => i.IssuedOn)
                                    ?? selected.OrderByDescending(i => i.IssuedOn))
            .ThenByDescending(i => i.CreatedAt)
            .ThenBy(i => i.Id);
        if (query.Offset > 0) page = page.Skip(query.Offset);
        if (query.Limit is { } limit) page = page.Take(limit);

        var invoices = await page.ToListAsync(ct);
        return new([.. invoices.Select(i => Row(i, names, query.Columns))], count, totals);
    }

    /// <summary>
    /// Где лежит каждая колонка таблицы. Описаны ВСЕ объявленные — это проверяет сам построитель;
    /// остальное — поля, которые заказчик дописал в тип, они лежат в <see cref="Invoice.Data" />.
    /// </summary>
    private static TableSql<Invoice> Sql(Dictionary<Guid, string> names) =>
        TableSql<Invoice>.Describe(InvoiceTable.Declaration, sql => sql
            .Text(InvoiceRequisites.NumberKey, i => i.Number)
            .Date(InvoiceRequisites.DateKey, i => i.IssuedOn)
            .Lookup(InvoiceRequisites.SupplierKey, i => i.SupplierId, names)
            .Lookup(InvoiceRequisites.PayerKey, i => i.PayerId, names)
            .Text(InvoiceRequisites.PurposeKey, i => i.Purpose)
            .Number(InvoiceRequisites.TotalKey, i => i.Total)
            .Number(InvoiceRequisites.VatTotalKey, i => i.VatTotal)
            .Date(InvoiceRequisites.ShippedOnKey, i => i.ShippedOn)
            .Number(InvoiceRequisites.DeferralKey, i => i.DeferralDays)
            .Date(InvoiceRequisites.DueDateKey, i => i.DueDate)
            .Lookup(InvoiceRequisites.StateKey, i => (InvoiceState?)i.State, States)
            .Lookup(InvoiceRequisites.PaymentKey, i => (InvoicePaymentState?)i.Payment, Payments)
            .Fields(key => i => i.Data.RootElement.GetProperty(key).GetString()));

    private static IReadOnlyDictionary<string, object?> Row(
        Invoice invoice, Dictionary<Guid, string> names, IReadOnlySet<string> open)
    {
        var row = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [InvoiceRequisites.NumberKey] = invoice.Number,
            [InvoiceRequisites.DateKey] = invoice.IssuedOn,
            [InvoiceRequisites.SupplierKey] = Name(invoice.SupplierId, names),
            [InvoiceRequisites.PayerKey] = Name(invoice.PayerId, names),
            [InvoiceRequisites.PurposeKey] = invoice.Purpose,
            [InvoiceRequisites.ShippedOnKey] = invoice.ShippedOn,
            [InvoiceRequisites.DeferralKey] = invoice.DeferralDays is { } days ? (decimal)days : null,
            [InvoiceRequisites.DueDateKey] = invoice.DueDate,
            [InvoiceRequisites.StateKey] = InvoiceRequisites.Label(invoice.State),
            [InvoiceRequisites.PaymentKey] = InvoiceRequisites.Label(invoice.Payment),
        };

        // Деньги — только открытые: закрытое ядро всё равно вычистит, но не считать его дешевле, чем
        // считать и выбрасывать.
        if (open.Contains(InvoiceRequisites.TotalKey)) row[InvoiceRequisites.TotalKey] = invoice.Total;
        if (open.Contains(InvoiceRequisites.VatTotalKey)) row[InvoiceRequisites.VatTotalKey] = invoice.VatTotal;

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
