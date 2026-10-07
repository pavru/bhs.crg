using BHS.CRG.Modules.Costs.Tables;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Где в счёте стоят ссылки не на месте и сколько их.</summary>
/// <param name="Kind">Место: <c>supplier</c>, <c>payer</c>, <c>type</c>, <c>position</c> (позиции строк),
/// <c>allocation</c> (разноска), <c>other</c> — колонка, которую этот список ещё не знает.</param>
/// <param name="Count">Ссылок. ⚠️ Не мест формы: удалённая стройка уносит и разделы, и одна часть
/// разноски даёт две ссылки — различить их по ответу ядра нечем.</param>
public sealed record InvoiceReferencePlace(string Kind, int Count);

/// <summary>
/// Что со ссылками счёта на записи ядра — для строки списка (issue #1186). В списке виден только
/// поставщик, и счёт с удалённой позицией или статьёй разноски выглядел бы чистым.
/// </summary>
/// <param name="SupplierLost">Удалена ли запись поставщика: <c>true</c> — удалена, <c>false</c> —
/// проверено, запись есть (названия нет — значит, её перевели в другой вид), <c>null</c> — поставщика
/// нет либо колонку проверить не удалось. ⚠️ Третье — не «на месте»: списку остаются осторожные слова.</param>
/// <param name="LostState">Что можно сделать с удалёнными записями счёта: <c>fixable</c>,
/// <c>locked</c> (закрытый период) или <c>type</c> (удалён только тип счёта); <c>null</c> — их нет.
/// Отбор списка «удалённые» показывает только первое.</param>
/// <param name="ArchivedCalls">Зовёт ли архивная запись к правке: счёт ещё не оплачен. По этому
/// признаку отбирает отбор «в архиве»; у оплаченного счёта пометка остаётся, но тихой.</param>
public sealed record InvoiceListReferences(
    bool? SupplierLost, string? LostState, bool ArchivedCalls,
    IReadOnlyList<InvoiceReferencePlace> Lost, IReadOnlyList<InvoiceReferencePlace> Archived)
{
    public const string Fixable = "fixable";
    public const string Locked = "locked";
    public const string TypeOnly = "type";

    /// <summary>Отборы списка — значения параметра <c>fix</c>. Те же коды, что у готовых отборов
    /// таблицы счетов: числа на чипах рейла берутся оттуда.</summary>
    public const string FixLost = "lost";
    public const string FixArchived = "archived";
}

/// <summary>
/// Пометки строк списка счетов из ответа обратного опроса ядра.
///
/// <para>Суждение — у <see cref="InvoiceReferenceTrouble" />, здесь только раскладка по счетам: посчитай
/// список своё, пометка строки разошлась бы с отбором, под которым она стоит.</para>
/// </summary>
public sealed class InvoiceListMarks
{
    private const string SupplierColumn = "supplier_id";

    private readonly InvoiceTroubles troubles;
    private readonly ILookup<Guid, ReferenceFinding> found;
    private readonly bool supplierChecked;

    public InvoiceListMarks(InvoiceTroubles troubles)
    {
        this.troubles = troubles;
        found = troubles.Findings.Found
            .Where(f => f is { DocumentKey: not null, DocumentTable: InvoiceReferenceTrouble.Invoices })
            .ToLookup(f => f.DocumentKey!.Value);
        supplierChecked = !troubles.Findings.Unchecked
            .Any(u => u is { Table: InvoiceReferenceTrouble.Invoices, Column: SupplierColumn });
    }

    /// <summary>Счета под отбором «удалённые записи»: только те, что можно исправить.</summary>
    public Guid[] Fixable => troubles.With(LostMark.Fixable);

    /// <summary>Счета со ссылкой в архив. Оплату отбор сверяет по самому счёту.</summary>
    public Guid[] Archived => [.. troubles.Archived];

    public InvoiceListReferences Of(Data.Invoice invoice)
    {
        var mine = found[invoice.Id].ToList();
        var lost = mine.Where(f => f.State == ReferenceState.Lost).ToList();

        return new(
            invoice.SupplierId is null || !supplierChecked
                ? null
                : lost.Any(f => f is { Table: InvoiceReferenceTrouble.Invoices, Column: SupplierColumn }),
            troubles.Lost.TryGetValue(invoice.Id, out var mark)
                ? mark switch
                {
                    LostMark.Fixable => InvoiceListReferences.Fixable,
                    LostMark.Locked => InvoiceListReferences.Locked,
                    _ => InvoiceListReferences.TypeOnly,
                }
                : null,
            troubles.Archived.Contains(invoice.Id)
                && InvoiceReferenceTrouble.ArchivedMarkOf(invoice) == ArchivedMark.Open,
            Places(lost),
            Places(mine.Where(f => f.State == ReferenceState.Archived)));
    }

    private static IReadOnlyList<InvoiceReferencePlace> Places(IEnumerable<ReferenceFinding> findings) =>
        [.. findings.GroupBy(Kind).OrderBy(g => Array.IndexOf(Order, g.Key))
            .Select(g => new InvoiceReferencePlace(g.Key, g.Sum(f => f.Rows)))];

    private static readonly string[] Order = ["supplier", "payer", "type", "position", "allocation", "other"];

    private static string Kind(ReferenceFinding finding) => (finding.Table, finding.Column) switch
    {
        (InvoiceReferenceTrouble.Invoices, SupplierColumn) => "supplier",
        (InvoiceReferenceTrouble.Invoices, "payer_id") => "payer",
        _ when InvoiceReferenceTrouble.IsType(finding) => "type",
        ("invoice_lines", _) => "position",
        ("invoice_allocations", _) => "allocation",
        _ => "other",
    };
}
