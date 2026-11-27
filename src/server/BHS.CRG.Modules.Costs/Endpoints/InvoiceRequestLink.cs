using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Costs.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

internal static class InvoiceRequestLink
{
    public static async Task<bool> SetAsync(
        CostsDbContext db, Invoice invoice, JsonElement supplied, CancellationToken ct)
    {
        if (supplied.ValueKind == JsonValueKind.Undefined) return false;
        Guid? selected = supplied.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when supplied.TryGetGuid(out var id) && id != Guid.Empty => id,
            _ => throw new InvalidRequestException("В purchaseRequestId укажите идентификатор заявки или null."),
        };
        if (selected == invoice.PurchaseRequestId) return false;

        if (selected is { } requestId)
        {
            // Пока фиксируется ссылка, заявку нельзя вернуть в черновик, закрыть или удалить.
            await PurchaseRequestModel.LockAsync(db, requestId, ct);
            var request = await db.PurchaseRequests.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == requestId, ct);
            if (request is null)
                throw new UnprocessableRequestException("Заявка на закупку не найдена.");
            if (request.Status != PurchaseRequestStatus.Submitted)
                throw new UnprocessableRequestException(request.Status == PurchaseRequestStatus.Closed
                    ? "Заявка закрыта. Новые счета можно связать только с поданной заявкой."
                    : "Заявка в черновике. Сначала подайте её.");
        }

        invoice.LinkPurchaseRequest(selected);
        return true;
    }

    public static async Task EnsureAllocationAsync(
        CostsDbContext db, Invoice invoice, AllocationPlacesSource places, decimal tolerance,
        bool alreadyLinked, CancellationToken ct)
    {
        var parts = await db.InvoiceAllocations.AsNoTracking().Where(part => part.InvoiceId == invoice.Id).ToListAsync(ct);
        var lines = await InvoiceLineEndpoints.StoredLinesAsync(db, invoice, ct);
        var known = parts.Count == 0 ? AllocationPlaces.None : await places.LoadAsync(ct);
        var allocation = InvoiceAllocations.Read(invoice, lines, parts, known, tolerance);
        await EnsureAllocationAsync(db, invoice, parts, allocation.Summary.Allocated, alreadyLinked, ct);
    }

    public static async Task EnsureAllocationAsync(
        CostsDbContext db, Invoice invoice, IReadOnlyList<InvoiceAllocation> parts, bool allocated,
        bool alreadyLinked, CancellationToken ct)
    {
        var site = await db.PurchaseRequests.AsNoTracking().Where(row => row.Id == invoice.PurchaseRequestId)
            .Select(row => row.ConstructionId).SingleAsync(ct);

        var reason = parts.Count == 0 ? "Счёт ещё не разнесён."
            : parts.Any(part => part.ArticleId is not null) ? "В разноске счёта есть статья вне строек."
            : parts.Any(part => part.ConstructionId != site) ? "В разноске счёта есть другая стройка."
            : !allocated ? "Счёт не полностью разнесён на стройку заявки."
            : null;
        if (reason is not null)
            throw new UnprocessableRequestException(reason + (alreadyLinked
                ? " Чтобы изменить стройку или оставить неполную разноску, сначала снимите ссылку на заявку."
                : " Связать можно только счёт, полностью разнесённый на стройку заявки."));
    }
}
