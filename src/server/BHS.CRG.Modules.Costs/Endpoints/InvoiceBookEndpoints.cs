using BHS.CRG.Modules.Costs.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Запись книги проводок счёта — для истории на форме (E2, issue #1100, PR 6b-2).
/// </summary>
/// <param name="Seq">Номер записи в книге счёта.</param>
/// <param name="Kind"><c>opening</c>, <c>correction</c>, <c>payment</c>, <c>unpayment</c>, <c>alignment</c> —
/// теми же словами, что предпросмотр исправления.</param>
/// <param name="Day">День записи: им исправление легло в книгу.</param>
/// <param name="At">Когда записано.</param>
/// <param name="Author">Имя автора на момент записи; <c>null</c> — запись старше этого поля.</param>
/// <param name="Reason">Причина: у исправления и отмены оплаты — от человека, у выравнивания — «допуск X → Y».</param>
/// <param name="Version">Номер версии, ушедшей в историю с исправлением; у прочих видов — <c>null</c>.</param>
/// <param name="Gross">Сумма строк записи с НДС: у открытия — деньги счёта целиком, у прочих — сдвиг.</param>
/// <param name="Net">То же без НДС.</param>
public sealed record InvoiceBookEntryView(
    int Seq, string Kind, DateOnly Day, DateTimeOffset At, string? Author, string? Reason, int? Version,
    decimal Gross, decimal Net, IReadOnlyList<CorrectionLineView> Lines);

/// <summary>
/// История счёта — его книга проводок (E2, issue #1100, PR 6b-2): кто, когда и почему сдвинул деньги
/// оплаченного счёта. Читают все, кому открыт счёт: история — часть счёта, как его строки, и права
/// исправлять для неё не нужно.
///
/// <para>Только показ: «на момент закрытия» считает <see cref="CostViews" />, а не этот адрес. Записи — по
/// порядку книги; что показывать первым, решает форма.</para>
/// </summary>
public static class InvoiceBookEndpoints
{
    private const string Read = "costs.invoice.read";

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/costs/invoices/{id:guid}/book", BookAsync)
            .WithTags("Счета на оплату")
            .RequireAuthorization(AppPolicies.Permission(Read));

    private static async Task<Ok<IReadOnlyList<InvoiceBookEntryView>>> BookAsync(
        Guid id, CostsDbContext db, CancellationToken ct)
    {
        // Удалённый счёт — 404, как у чтения счёта, а не пустая история: пустая читалась бы «исправлений
        // не было» у счёта, которого нет.
        await InvoiceEndpoints.FindAsync(db, id, ct);

        IReadOnlyList<InvoiceBookEntryView> entries = [.. (await InvoiceBook.HistoryAsync(db, id, ct)).Select(h =>
            new InvoiceBookEntryView(h.Entry.Seq, InvoiceCorrections.Kind(h.Entry.Kind), h.Entry.Day, h.Entry.At,
                h.Entry.AuthorName, h.Entry.Reason, h.Entry.Version,
                h.Lines.Sum(l => l.Gross), h.Lines.Sum(l => l.Net),
                [.. h.Lines.Select(l => InvoiceCorrections.Line(l.Value))]))];
        return TypedResults.Ok(entries);
    }
}
