using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Costs.Tables;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
/// <param name="SupplierLost">Опрос ядра подтвердил: запись поставщика удалена. ⚠️ <c>false</c> — НЕ
/// «запись на месте» и не «её перевели в другой вид»: это лишь «потеря не найдена». Названия может не
/// быть и потому, что вида «Организация» на установке нет, и потому, что запись удалили между опросом
/// и чтением счетов (снимки разные). Списку тогда остаются осторожные слова (ревью PR #1241).</param>
/// <param name="LostState">Что можно сделать с удалёнными записями счёта: <c>fixable</c>,
/// <c>locked</c> (закрытый период) или <c>type</c> (удалён только тип счёта); <c>null</c> — их нет.
/// Отбор списка «удалённые» показывает только первое.</param>
/// <param name="ArchivedCalls">Зовёт ли архивная запись к правке: счёт ещё не оплачен. По этому
/// признаку отбирает отбор «в архиве»; у оплаченного счёта пометка остаётся, но тихой.</param>
public sealed record InvoiceListReferences(
    bool SupplierLost, string? LostState, bool ArchivedCalls,
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
/// Числа для чипов «наведите порядок» над списком счетов — одним ответом и одним опросом ядра.
/// </summary>
/// <param name="Lost">Счетов с удалённой записью, которые можно исправить. То же число, что у готового
/// отбора таблицы счетов «lost», и столько же строк отдаёт список под <c>fix=lost</c>.</param>
/// <param name="Archived">Неоплаченных счетов с записью из архива — как у готового отбора «archived».</param>
/// <param name="Locked">Счетов с удалённой записью в закрытом периоде: в <paramref name="Lost" /> не
/// входят, исправить их нельзя — и промолчать о них значило бы сказать «больше нет».</param>
/// <param name="Doubt">Почему числам нельзя верить как полным; <c>null</c> — проверено всё.
/// ⚠️ Ноль с этой причиной — не «счетов нет».</param>
/// <param name="Unrecognized">Черновиков со сканом без строк, чей скан сейчас не читается, —
/// столько строк отдаёт список под <c>unrecognized=true</c>.</param>
public sealed record InvoiceQueuesView(int Lost, int Archived, int Locked, string? Doubt, int Unrecognized = 0);

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

    public InvoiceListMarks(InvoiceTroubles troubles)
    {
        this.troubles = troubles;
        found = troubles.Findings.Found
            .Where(f => f is { DocumentKey: not null, DocumentTable: InvoiceReferenceTrouble.Invoices })
            .ToLookup(f => f.DocumentKey!.Value);
    }

    /// <summary>
    /// Пометки для списка. <b>Список от опроса не зависит</b>: пометки — вспомогательные данные, и
    /// отказ опроса (второе соединение, границы периодов) не должен отнимать у человека сами счета.
    /// Тогда пометок нет (<c>null</c>), а о несчитанных числах скажет адрес чисел — своим отказом.
    /// Под отбором иначе: без ответа отбирать нечем, и отказ остаётся отказом (ревью PR #1241).
    /// </summary>
    public static async Task<InvoiceListMarks?> ReadAsync(
        InvoiceReferenceTrouble trouble, bool required, ILogger log, CancellationToken ct)
    {
        try
        {
            return new(await trouble.ReadAsync(withArchive: true, ct));
        }
        catch (Exception e) when (!required && e is not OperationCanceledException)
        {
            log.LogWarning(e, "Список счетов отдан без пометок ссылок: обратный опрос ядра отказал");
            return null;
        }
    }

    /// <summary>
    /// Числа чипов над списком (<c>GET /api/costs/invoices/queues</c>) — одним опросом на все три.
    /// Отдельным адресом, а не в ответе списка: список отдаётся массивом, и на нём стоят прогоны и посев.
    /// </summary>
    public static async Task<Ok<InvoiceQueuesView>> QueuesAsync(
        CostsDbContext db, InvoiceReferenceTrouble trouble, InvoiceListRecognition recognition, CancellationToken ct)
    {
        var troubles = await trouble.ReadAsync(withArchive: true, ct);
        var archived = troubles.Archived.ToArray();
        var keys = db.Database.SqlQuery<Guid>($"SELECT unnest({archived}) AS \"Value\"");
        return TypedResults.Ok(new InvoiceQueuesView(
            troubles.With(LostMark.Fixable).Length,
            archived.Length == 0 ? 0 : await db.Invoices.AsNoTracking()
                .CountAsync(i => keys.Contains(i.Id) && i.Payment != InvoicePaymentState.Paid, ct),
            troubles.With(LostMark.Locked).Length,
            troubles.Doubt,
            InvoiceListRecognition.Unrecognized(await recognition.ReadAsync(ct)).Length));
    }

    /// <summary>Счета под отбором «удалённые записи»: только те, что можно исправить.</summary>
    public Guid[] Fixable => troubles.With(LostMark.Fixable);

    /// <summary>Счета со ссылкой в архив. Оплату отбор сверяет по самому счёту.</summary>
    public Guid[] Archived => [.. troubles.Archived];

    public InvoiceListReferences Of(Invoice invoice)
    {
        var mine = found[invoice.Id].ToList();
        var lost = mine.Where(f => f.State == ReferenceState.Lost).ToList();

        return new(
            lost.Any(f => f is { Table: InvoiceReferenceTrouble.Invoices, Column: SupplierColumn }),
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
