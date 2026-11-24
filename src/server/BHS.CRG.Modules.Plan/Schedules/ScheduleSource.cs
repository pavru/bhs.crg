using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Distribution;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Schedules;

internal sealed record SchedulePart(Guid RowId, PlanItemKey Item, string Name, decimal? Volume)
{
    public (Guid, Guid?) Key => (RowId, Item.SectionId);
}

internal sealed record ScheduleSource(IReadOnlyList<SchedulePart> Parts, int Unmapped, string Fingerprint)
{
    private sealed record Row(Guid Id, string Name, string? LsrSection, decimal? SourceVolume, decimal? Volume,
        decimal? Factor, Guid? PlanItemId, Guid? WorkTypeId, Guid? UnitId);

    // Строки выбранной редакции, включая прежнюю, без денежных полей.
    public static async Task<ScheduleSource> ReadAsync(
        PlanDbContext db, Guid revision, Guid construction, Guid? estimateSection, CancellationToken ct)
    {
        var rows = await db.EstimateRows.AsNoTracking().Where(r => r.RevisionId == revision && r.Kind == EstimateRowKind.Work)
            .OrderBy(r => r.Order).ThenBy(r => r.Id)
            .Select(r => new Row(r.Id, r.Name, r.LsrSectionCode, r.SourceVolume, r.Volume, r.Factor,
                r.PlanItemId, r.WorkTypeId, r.UnitId))
            .ToListAsync(ct);
        var mapped = rows.Where(r => r.PlanItemId.HasValue && r.WorkTypeId.HasValue && r.UnitId.HasValue).ToArray();
        var unmapped = rows.Count - mapped.Length;
        IReadOnlyList<SchedulePart> parts;
        if (estimateSection is { } section)
            parts = mapped.Select(row => Part(row, construction, section, row.Volume)).ToList();
        else
        {
            var rules = await db.DistributionRules.AsNoTracking().Where(r => r.RevisionId == revision).ToListAsync(ct);
            var portions = await db.DistributionParts.AsNoTracking().Where(r => r.RevisionId == revision).ToListAsync(ct);
            var distribution = DistributionLines.Evaluate(
                [.. mapped.Select(r => new LineInput(r.Id, r.LsrSection, r.SourceVolume))], rules, portions);
            parts = [.. mapped.Zip(distribution.Lines).SelectMany(pair => Split(pair.First, pair.Second, construction))];
        }

        return new(parts, unmapped, Hash(string.Join('\n', parts.OrderBy(p => p.RowId).ThenBy(p => p.Item.SectionId)
            .Select(p => FormattableString.Invariant(
                $"{p.RowId:N}:{p.Item.WorkTypeId:N}:{p.Item.UnitId:N}:{p.Item.SectionId:N}:{Quantity(p.Volume)}"))
            .Prepend(unmapped.ToString(CultureInfo.InvariantCulture)))));
    }

    private static IEnumerable<SchedulePart> Split(Row row, LineState distribution, Guid construction)
    {
        if (distribution.Source != LineSource.Parts)
        {
            Guid? section = distribution.Targets.Count > 0 ? distribution.Targets[0] : null;
            yield return Part(row, construction, section, row.Volume);
            yield break;
        }

        foreach (var part in distribution.Parts.OrderBy(p => p.SectionId))
            yield return Part(row, construction, part.SectionId, DistributionMath.ToPlan(part.Quantity, row.Factor));
        if (distribution.Rest is > 0m)
            yield return Part(row, construction, null, DistributionMath.ToPlan(distribution.Rest.Value, row.Factor));
    }

    private static SchedulePart Part(Row row, Guid construction, Guid? section, decimal? volume) =>
        new(row.Id, new PlanItemKey(row.WorkTypeId!.Value, construction, section, row.UnitId!.Value), row.Name, volume);

    private static string? Quantity(decimal? volume) => volume?.ToString("G29", CultureInfo.InvariantCulture);
    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
