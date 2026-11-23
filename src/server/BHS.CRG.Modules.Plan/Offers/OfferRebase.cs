using System.Security.Cryptography;
using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Offers;

internal sealed record PreparedOfferRebase(
    EstimateRevision Target, List<OfferLine> Lines, OfferRebasePreviewDto Preview)
{
    internal IEnumerable<OfferRebaseLineDto> Removed =>
        Preview.Removed.Concat(Preview.Unmatched.SelectMany(group => group.Before));
}

/// <summary>Предпросмотр и перенос используют одни и те же строки и расчёт цены (PLAN-13).</summary>
internal static class OfferRebase
{
    internal static async Task<PreparedOfferRebase> PrepareAsync(
        PlanDbContext db, OfferDesk desk, Offer offer, string version, Guid? revisionId, bool cost,
        CancellationToken ct, bool fallbackToCurrent = false)
    {
        var sourceId = offer.RevisionId
            ?? throw new ConflictException("Сменить редакцию можно у КП из сметы. У КП по прайс-листу редакции нет.");
        var revisions = (await OfferDrift.ReadAsync(db, [sourceId], ct))[sourceId];
        var targetId = revisionId ?? revisions.Current?.Id
            ?? throw new ConflictException("У сметы нет действующей редакции. Назовите целевую редакцию.");
        var target = await db.EstimateRevisions
            .FirstOrDefaultAsync(r => r.Id == targetId && r.EstimateId == revisions.Source.EstimateId, ct);
        if (target is null && fallbackToCurrent && revisions.Current is { } current)
            target = await db.EstimateRevisions.FirstAsync(r => r.Id == current.Id, ct);
        if (target is null)
            throw new InvalidRequestException("Целевой редакции нет в этой смете. Перечитайте редакции сметы.");

        // Версия цели меняется при сопоставлении строк. Закрытая себестоимость в отпечаток не входит.
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            offer.Id, Version = version, Source = sourceId, Target = target.Id,
            TargetVersion = db.VersionOf(target), Current = revisions.Current?.Id, Latest = revisions.Latest.Id,
            offer.Markup,
            Sections = offer.SectionMarkups.OrderBy(s => s.SectionCode, StringComparer.Ordinal).Select(s => new { s.SectionCode, s.Markup }),
            Lines = offer.Lines.OrderBy(l => l.Id).Select(l => new { l.Id, l.Markup, l.Price, l.Amount }),
        })));
        var draft = new OfferRebasePreviewDto(version, new(sourceId, revisions.Source.Number), new(target.Id, target.Number),
            fingerprint, [], [], [], [],
            cost ? null : [new("cost", PlanModule.EstimateCost, PlanModule.EstimateCostGives)]);
        if (sourceId == target.Id) return new(target, [], draft);

        var sourceRows = (await desk.RevisionRowsAsync(sourceId, ct)).ToDictionary(row => row.Id);
        var targetRows = await desk.RevisionRowsAsync(target.Id, ct);
        return Compare(offer, sourceRows, target, targetRows, cost, draft);
    }

    private static PreparedOfferRebase Compare(
        Offer offer, Dictionary<Guid, EstimateRow> sourceRows, EstimateRevision target, IEnumerable<EstimateRow> targetRows,
        bool cost, OfferRebasePreviewDto preview)
    {
        var sections = offer.SectionMarkups.ToDictionary(s => s.SectionCode, s => s.Markup, StringComparer.Ordinal);
        var before = offer.Lines.OrderBy(l => l.Order)
            .Select(line => (Line: line, Row: sourceRows[line.RevisionRowId!.Value])).ToLookup(x => RowKey.Of(x.Row));
        var after = targetRows.ToLookup(RowKey.Of);
        var lines = new List<OfferLine>();
        var added = new List<OfferRebaseLineDto>();
        var removed = new List<OfferRebaseLineDto>();
        var changed = new List<OfferRebaseChangeDto>();
        var unmatched = new List<OfferRebaseUnmatchedDto>();

        foreach (var key in before.Select(g => g.Key).Union(after.Select(g => g.Key)))
        {
            var old = before[key];
            var next = after[key];
            if (key.Number is not null && old.Count() == 1 && next.Count() == 1)
            {
                var (line, row) = old.Single();
                var replacement = next.Single();
                var newLine = BuildLine(replacement, line.Markup);
                var was = View(line, row, line.Id, cost);
                var willBe = View(newLine, replacement, null, cost);
                if (was with { RevisionRowId = willBe.RevisionRowId, LineId = null } != willBe)
                    changed.Add(new(was, willBe));
                continue;
            }

            var beforeGroup = old.Select(x => View(x.Line, x.Row, x.Line.Id, cost)).ToList();
            var afterGroup = next.Select(r => View(BuildLine(r, null), r, null, cost)).ToList();
            if (beforeGroup.Count == 0) added.AddRange(afterGroup);
            else if (afterGroup.Count == 0) removed.AddRange(beforeGroup);
            else unmatched.Add(new(beforeGroup, afterGroup));
        }

        return new(target, lines, preview with { Added = added, Removed = removed, Changed = changed, Unmatched = unmatched });

        OfferLine BuildLine(EstimateRow row, decimal? ownMarkup)
        {
            var line = OfferLine.FromRevisionRow(offer.Id, row);
            line.ChangeMarkup(ownMarkup);
            var effective = OfferPricing.Effective(ownMarkup, line.LsrSectionCode, sections, offer.Markup);
            var price = OfferPricing.Price(row.Total, line.Volume, effective.Value);
            line.Reprice(price.Price, price.Amount);
            lines.Add(line);
            return line;
        }
    }

    private static OfferRebaseLineDto View(OfferLine line, EstimateRow row, Guid? lineId, bool cost) => new(
        row.Id, lineId, line.Number,
        line.LsrSectionCode is null && line.LsrSectionName is null ? null : new(line.LsrSectionCode, line.LsrSectionName),
        line.Name, line.Volume, line.Unit, line.WorkTypeId, line.Material ? "material" : "work",
        line.Markup, line.Price, line.Amount,
        cost ? new(row.Total, row.Total is { } basis ? line.Amount - basis : null) : null);

    private sealed record RowKey(
        Guid? PlanItemId, EstimateRowKind Kind, string? SectionCode, string? SectionName,
        string? Rate, string? Name, string? Number)
    {
        internal static RowKey Of(EstimateRow row)
        {
            var rate = Clean(row.RateCode);
            return new(row.PlanItemId, row.Kind, Clean(row.LsrSectionCode), Clean(row.LsrSectionName),
                rate, rate is null ? row.Name.Trim() : null, Clean(row.NumberInSection));
        }

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
