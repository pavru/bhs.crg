using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using BHS.CRG.Modules.Plan.Data;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>История отправленных версий — только как история, никогда как источник итогов (CORE-36).</summary>
internal static class OfferHistory
{
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
