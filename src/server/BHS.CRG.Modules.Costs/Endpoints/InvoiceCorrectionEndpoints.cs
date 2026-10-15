using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Исправление счёта закрытого периода (E2, issue #1100, spec v3.3 п. 1.4): предпросмотр и запись.
///
/// <para><b>Ворота — правка счёта</b> (<c>costs.invoice.edit</c>), а право исправления спрашивает связка:
/// человеку без него на обычном счёте нужно услышать «правьте как обычно», а не «нет права» (порядок
/// проверок — <see cref="InvoiceDesk" />, вход исправления).</para>
///
/// <para><b>Два адреса, одна функция.</b> Предпросмотр считает всё, что сделает запись, — версию, день
/// записи и строки книги, — и не сохраняет ничего; запись сверяет свою отметку с его. Оба требуют
/// версию формы: предпросмотр по устаревшей форме показал бы исправление не того счёта.</para>
/// </summary>
public static class InvoiceCorrectionEndpoints
{
    private const string Edit = CostsModule.InvoiceEdit;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/costs/invoices/{id:guid}/correction").WithTags("Счета на оплату");

        group.MapPost("/preview", PreviewAsync).RequireAuthorization(AppPolicies.Permission(Edit));
        group.MapPost("/", CorrectAsync).RequireAuthorization(AppPolicies.Permission(Edit));
    }

    /// <summary>Что запишет исправление — не записывая.</summary>
    private static async Task<Ok<CorrectionPreviewView>> PreviewAsync(
        Guid id, InvoiceCorrectionRequest body, InvoiceDesk desk, IModuleWriteGuard guard, CancellationToken ct)
    {
        var done = await desk.CorrectAsync(id, InvoiceCorrections.Parse(body, preview: true), guard, preview: true, ct);
        return TypedResults.Ok(InvoiceCorrections.Preview(done));
    }

    /// <summary>
    /// Исправить счёт: версия, учётные дни, книга — одной транзакцией. Журнал — после неё: он пишется
    /// соединением ядра и зафиксировался бы раньше модуля.
    /// </summary>
    private static async Task<Ok<InvoiceView>> CorrectAsync(
        Guid id, InvoiceCorrectionRequest body, InvoiceDesk desk, IModuleWriteGuard guard, IModuleActivityLog log,
        CancellationToken ct)
    {
        var parsed = InvoiceCorrections.Parse(body, preview: false);
        var done = await desk.CorrectAsync(id, parsed, guard, preview: false, ct);
        var invoice = done.Invoice;
        var label = InvoiceEndpoints.Label(invoice);

        await log.RecordAsync(InvoiceActions.Corrected, invoice.Id.ToString(), label,
            before: $"версия {done.Version}", after: parsed.Reason, ct: ct);

        // Изменённые поля шапки и ручной срок — теми же событиями, что у правки шапки (ревью PR #1364).
        await InvoiceEndpoints.RecordRequisitesAsync(log, invoice, done.Changed, done.DueWas, ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }
}
