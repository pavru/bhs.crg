using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Оплата счёта (задача C5 этапа 2, issue #1082, ТЗ COST-4, COST-9, COST-16).
///
/// <para><b>Оплата — целиком, одним платежом, в одну дату</b> (решение владельца 04.10.2026). У платежа
/// одна фактическая дата, а учётных — по числу контуров: у каждой доли разноски своя, по её стройке.
/// Платёж задним числом в закрытый месяц не отказ, а перенос в первый открытый день — и форма говорит
/// это ДО сохранения.</para>
///
/// <para><b>Своё право — <c>costs.invoice.pay</c></b>: отметка меняет цифры отчётов и учётный период.
/// Читать записанный расклад может тот, кто читает счёт.</para>
///
/// <para><b>Версий оплата не создаёт</b> — это событие журнала; отмена ошибочной отметки — тоже.</para>
/// </summary>
public static class PaymentEndpoints
{
    private const string Pay = "costs.invoice.pay";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/costs/invoices/{id:guid}").WithTags("Счета на оплату");

        group.MapGet("/paid", PostedAsync).RequireAuthorization(AppPolicies.Permission("costs.invoice.read"));
        group.MapPost("/paid/preview", PreviewAsync).RequireAuthorization(AppPolicies.Permission(Pay));
        group.MapPost("/paid", PayAsync).RequireAuthorization(AppPolicies.Permission(Pay));
        group.MapPut("/paid", DescribeAsync).RequireAuthorization(AppPolicies.Permission(Pay));
        group.MapPost("/unpaid", CancelAsync).RequireAuthorization(AppPolicies.Permission(Pay));
    }

    /// <summary>
    /// Показать расклад оплаты, не записывая: какая доля каким днём войдёт в затраты.
    ///
    /// <para>⚠️ Отвечает ТЕМИ ЖЕ отказами, что запись, — полем ответа, а не кодом: человек, заполнивший
    /// диалог, не должен узнавать о несведённом счёте после нажатия (ревизия Дизайнера).</para>
    /// </summary>
    private static async Task<Ok<PaymentPostingView>> PreviewAsync(
        Guid id, PaymentPreviewRequest body, CostsDbContext db, AllocationPlacesSource places,
        IModulePeriods periods, IModuleClock clock, CancellationToken ct)
    {
        var invoice = await InvoiceEndpoints.FindAsync(db, id, ct);
        if (invoice.Payment == InvoicePaymentState.Paid)
            throw new ConflictException(
                $"{InvoiceEndpoints.Label(invoice)} уже оплачен {invoice.PaidOn:dd.MM.yyyy}. Дату меняют отменой " +
                "оплаты и новой отметкой.");

        var today = await clock.TodayAsync(ct);
        return TypedResults.Ok(await PostingAsync(db, places, invoice, body.PaidOn ?? today, today,
            PostedBefore.None, await periods.BoundariesAsync(ct), ct));
    }

    /// <summary>Записанный расклад оплаченного счёта — той же функцией и в том же виде, что предпросмотр.</summary>
    private static async Task<Ok<PaymentPostingView>> PostedAsync(
        Guid id, CostsDbContext db, AllocationPlacesSource places, IModulePeriods periods, IModuleClock clock,
        CancellationToken ct)
    {
        var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new NotFoundException("Счёт не найден.");
        if (invoice.PaidOn is not { } paidOn)
            throw new NotFoundException($"{InvoiceEndpoints.Label(invoice)} не оплачен — расклада оплаты у него нет.");

        var parts = await db.InvoiceAllocations.AsNoTracking().Where(a => a.InvoiceId == invoice.Id).ToListAsync(ct);
        return TypedResults.Ok(await PostingAsync(db, places, invoice, paidOn, await clock.TodayAsync(ct),
            PaymentPosting.Before(invoice, parts), await periods.BoundariesAsync(ct), ct));
    }

    /// <summary>
    /// Отметить оплату.
    ///
    /// <para>⚠️ Расклад считается ЗАНОВО, под замком, и сверяется с увиденным по отметке: между
    /// предпросмотром и записью период могли закрыть, разноску — поправить, сумму — сменить. Разошлось —
    /// отказ 409, и форма показывает свежий расклад; записать молча другое, чем обещано, нельзя.</para>
    /// </summary>
    private static async Task<Ok<InvoiceView>> PayAsync(
        Guid id, PaymentRequest body, CostsDbContext db, InvoiceDesk desk, AllocationPlacesSource places,
        IModuleClock clock, IModuleUser user, IModuleActivityLog log, CancellationToken ct)
    {
        if (body.PaidOn is not { } paidOn)
            throw new InvalidRequestException("Дата платежа не названа. Оплата — это «когда», и без даты у неё нет учётного периода.");
        if (body.Seen is not { Length: > 0 } seen)
            throw new InvalidRequestException(
                "Отметка расклада («seen») не прислана. Берётся из предпросмотра оплаты («stamp»): без неё оплату " +
                "подтверждал бы человек, а записывался бы расклад, которого он не видел.");

        var document = Text(body.Document);
        var today = await clock.TodayAsync(ct);

        var (invoice, moved) = await desk.WriteAsync(id, async write =>
        {
            var invoice = write.Invoice;
            if (invoice.Payment == InvoicePaymentState.Paid)
                throw new ConflictException(
                    $"{InvoiceEndpoints.Label(invoice)} уже оплачен {invoice.PaidOn:dd.MM.yyyy}. Дату меняют отменой " +
                    "оплаты и новой отметкой.");

            var posting = await PostingAsync(db, places, invoice, paidOn, today, PostedBefore.None, write.Boundaries, ct);
            if ((posting.Refusal ?? posting.DateRefusal) is { } why)
                throw new InvalidRequestException($"{InvoiceEndpoints.Label(invoice)}: оплатить нельзя — {why}.");
            if (posting.Stamp != seen)
                throw new ConflictException(
                    $"{InvoiceEndpoints.Label(invoice)}: расклад оплаты изменился, пока диалог был открыт, — закрыли " +
                    "период, поправили разноску или сумму счёта. Оплата не записана: посмотрите расклад заново.");

            invoice.Pay(paidOn, document, user.Id);
            await db.SaveChangesAsync(ct);
            return (invoice, posting.Rows.Where(r => r.Moved).ToList());
        }, ct);

        // Сумм в журнале ядра нет нарочно: его читают по core.audit.read, без права на счета.
        await log.RecordAsync(InvoiceActions.Paid, invoice.Id.ToString(), InvoiceEndpoints.Label(invoice),
            after: $"оплачен {paidOn:dd.MM.yyyy}" + (document is null ? "" : $"; платёжный документ: {document}") +
                (moved.Count == 0 ? "" : "; учётная дата перенесена: " +
                    string.Join(", ", moved.Select(r => $"{r.Name} — {r.AccountingOn:dd.MM.yyyy}"))),
            ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }

    /// <summary>Поправить платёжный документ. Дату так не меняют: она задаёт учётные даты долей.</summary>
    private static async Task<Ok<InvoiceView>> DescribeAsync(
        Guid id, PaymentDocumentRequest body, CostsDbContext db, InvoiceDesk desk, IModuleActivityLog log,
        CancellationToken ct)
    {
        var document = Text(body.Document);

        var (invoice, was) = await desk.WriteAsync(id, async write =>
        {
            var invoice = write.Invoice;
            EnsurePaid(invoice);

            var was = invoice.PaymentDocument;
            if (was != document)
            {
                invoice.DescribePayment(document);
                await db.SaveChangesAsync(ct);
            }

            return (invoice, was);
        }, ct);

        if (was != document)
            await log.RecordAsync(InvoiceActions.PaymentDescribed, invoice.Id.ToString(),
                InvoiceEndpoints.Label(invoice), before: was ?? "не указан", after: document ?? "не указан", ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }

    /// <summary>
    /// Отменить ошибочную отметку оплаты. В закрытом периоде — отказ (его даёт связка записи): пока нет
    /// версий, сначала отменяют закрытие.
    /// </summary>
    private static async Task<Ok<InvoiceView>> CancelAsync(
        Guid id, PaymentCancelRequest body, CostsDbContext db, InvoiceDesk desk, IModuleActivityLog log,
        CancellationToken ct)
    {
        if (Text(body.Reason) is not { } reason)
            throw new InvalidRequestException(
                "Причина отмены не названа. Отмена оплаты версий не создаёт — причина остаётся единственным " +
                "следом того, почему счёт перестал быть оплаченным.");

        var (invoice, was) = await desk.WriteAsync(id, async write =>
        {
            var invoice = write.Invoice;
            EnsurePaid(invoice);

            var was = invoice.PaidOn!.Value;
            invoice.Unpay();
            await db.SaveChangesAsync(ct);
            return (invoice, was);
        }, ct);

        await log.RecordAsync(InvoiceActions.Unpaid, invoice.Id.ToString(), InvoiceEndpoints.Label(invoice),
            before: $"оплачен {was:dd.MM.yyyy}", after: $"не оплачен. Причина: {reason}", ct: ct);

        return TypedResults.Ok(await desk.ViewAsync(invoice, ct));
    }

    /// <summary>
    /// Расклад оплаты счёта — один на предпросмотр, запись и чтение записанного: считает его
    /// <see cref="PaymentPosting.Plan" />, и считает по тем границам, которые ему дали.
    /// </summary>
    private static async Task<PaymentPostingView> PostingAsync(
        CostsDbContext db, AllocationPlacesSource places, Invoice invoice, DateOnly paidOn, DateOnly today,
        PostedBefore kept, PeriodBoundaries boundaries, CancellationToken ct)
    {
        var lines = await db.InvoiceLines.AsNoTracking().Where(l => l.InvoiceId == invoice.Id).ToListAsync(ct);
        var parts = await db.InvoiceAllocations.AsNoTracking().Where(a => a.InvoiceId == invoice.Id).ToListAsync(ct);
        var math = lines.Select(InvoiceAllocations.Line).ToList();

        var refusal = InvoiceDesk.Unpayable(invoice, PaymentPosting.Balance(math, parts, invoice.Total));
        var late = paidOn > today
            ? $"дата платежа {paidOn:dd.MM.yyyy} в будущем: сегодня {today:dd.MM.yyyy}"
            : null;
        if (refusal is not null || late is not null)
            return new PaymentPostingView(today, paidOn, invoice.Total, refusal, late, [], string.Empty);

        var total = invoice.Total!.Value;
        var plan = PaymentPosting.Plan(paidOn, total, math, parts, kept, boundaries);
        var known = parts.Count == 0 ? AllocationPlaces.None : await places.LoadAsync(ct);

        return new PaymentPostingView(today, paidOn, total, null, null,
            PaymentViews.Rows(plan, lines.ToDictionary(l => l.Id, l => l.Ordinal), known, boundaries),
            PaymentViews.Stamp(plan, total, InvoiceAllocations.Stamp(parts)));
    }

    private static void EnsurePaid(Invoice invoice)
    {
        if (invoice.Payment != InvoicePaymentState.Paid)
            throw new ConflictException(
                $"{InvoiceEndpoints.Label(invoice)} не оплачен. Так бывает, когда оплату отменили, пока форма была " +
                "открыта: перечитайте счёт.");
    }

    /// <summary>Пустая строка — «не указано», а не значение.</summary>
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
