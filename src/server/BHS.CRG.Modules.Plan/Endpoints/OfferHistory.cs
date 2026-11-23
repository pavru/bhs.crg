using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using BHS.CRG.Modules.Plan.Data;

namespace BHS.CRG.Modules.Plan.Endpoints;

internal sealed record HistoricalOfferSource(
    Guid RevisionId, Guid Id, string Number, int Year, int Sequence, int Version,
    string Status, DateOnly Date, DateOnly? SentOn, IReadOnlySet<Guid> RevisionRows);

/// <summary>История отправленных версий — только как история, никогда как источник итогов (CORE-36).</summary>
internal static class OfferHistory
{
    internal static async Task<List<HistoricalOfferSource>> ForRevisionsAsync(
        PlanDbContext db, Guid[] revisionIds, CancellationToken ct)
    {
        // Перенос КП допускается внутри одной сметы, поэтому достаточно истории КП этой сметы.
        var snapshots =
            from v in db.Set<OfferVersion>().AsNoTracking()
            join o in db.Offers.AsNoTracking() on v.RecordId equals o.Id
            where o.RevisionId != null && revisionIds.Contains(o.RevisionId.Value)
            select new { v.RecordId, v.Number, v.Content, o.Year, o.Sequence };

        var result = new List<HistoricalOfferSource>();
        var wanted = revisionIds.ToHashSet();
        await foreach (var v in snapshots.AsAsyncEnumerable().WithCancellation(ct))
        {
            using var doc = JsonDocument.Parse(v.Content);
            var content = doc.RootElement;
            if (!content.TryGetProperty("revisionId", out var source) || source.ValueKind != JsonValueKind.String ||
                !source.TryGetGuid(out var revisionId) || !wanted.Contains(revisionId)) continue;

            var rows = new HashSet<Guid>();
            foreach (var line in content.GetProperty("lines").EnumerateArray())
                if (line.TryGetProperty("revisionRowId", out var row) && row.ValueKind == JsonValueKind.String &&
                    row.TryGetGuid(out var rowId))
                    rows.Add(rowId);
            result.Add(new(revisionId, v.RecordId, content.GetProperty("number").GetString()!, v.Year, v.Sequence,
                v.Number, content.GetProperty("status").GetString()!, content.GetProperty("date").Deserialize<DateOnly>(),
                content.GetProperty("sentOn").Deserialize<DateOnly?>(), rows));
        }
        return result;
    }

    public static async Task<IResult> ReadAsync(Guid id, PlanDbContext db, CancellationToken ct)
    {
        if (!await db.Offers.AnyAsync(o => o.Id == id, ct)) throw OfferViews.NotFound();
        var versions = await db.Set<OfferVersion>().AsNoTracking().Where(v => v.RecordId == id)
            .OrderByDescending(v => v.Number).ToListAsync(ct);
        return Results.Ok(versions.Select(v =>
        {
            var content = JsonSerializer.Deserialize<JsonElement>(v.Content);
            return new OfferVersionDto(v.Number, content.GetProperty("status").GetString()!,
                content.GetProperty("sentOn").Deserialize<DateOnly?>(),
                content.GetProperty("money").GetProperty("total").GetDecimal(), v.At, v.AuthorId, content);
        }).ToList());
    }
}
