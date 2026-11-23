using System.Security.Cryptography;
using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Offers;

internal sealed record PreparedOfferRebase(
    EstimateRevision Target, List<OfferLine> Lines, IReadOnlyDictionary<string, decimal> SectionMarkups,
    OfferRebasePreviewDto Preview)
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
            Algorithm = 2,
            offer.Id, Version = version, Source = sourceId, Target = target.Id,
            TargetVersion = db.VersionOf(target), Current = revisions.Current?.Id, Latest = revisions.Latest.Id,
            offer.Markup,
            Sections = offer.SectionMarkups.OrderBy(s => s.SectionCode, StringComparer.Ordinal).Select(s => new { s.SectionCode, s.Markup }),
            Lines = offer.Lines.OrderBy(l => l.Id).Select(l => new { l.Id, l.Markup, l.Price, l.Amount }),
        })));
        var draft = new OfferRebasePreviewDto(version, new(sourceId, revisions.Source.Number), new(target.Id, target.Number),
            fingerprint, [], [], [], [], [],
            cost ? null : [new("cost", PlanModule.EstimateCost, PlanModule.EstimateCostGives)]);
        if (sourceId == target.Id) return new(target, [], new Dictionary<string, decimal>(), draft);

        var sourceRows = await desk.RevisionRowsAsync(sourceId, ct);
        var targetRows = await desk.RevisionRowsAsync(target.Id, ct);
        return Compare(offer, sourceRows, target, targetRows, cost, draft);
    }

    private static PreparedOfferRebase Compare(
        Offer offer, IEnumerable<EstimateRow> sourceRows, EstimateRevision target, IEnumerable<EstimateRow> targetRows,
        bool cost, OfferRebasePreviewDto preview)
    {
        var sectionGroups = OfferRebaseMatching.Sections(sourceRows, targetRows).ToList();
        var (sections, unmatchedSectionMarkups) = SectionMarkups(offer, sectionGroups);
        var before = offer.Lines.ToLookup(l => l.RevisionRowId);
        var lines = new List<OfferLine>();
        var added = new List<OfferRebaseLineDto>();
        var removed = new List<OfferRebaseLineDto>();
        var changed = new List<OfferRebaseChangeDto>();
        var unmatched = new List<OfferRebaseUnmatchedDto>();

        foreach (var group in sectionGroups.SelectMany(OfferRebaseMatching.Rows))
        {
            var old = group.Before.SelectMany(row => before[row.Id].Select(line => (Line: line, Row: row))).ToList();
            if (group.Paired && old.Count == 1)
            {
                var (line, row) = old.Single();
                var replacement = group.After.Single();
                var newLine = BuildLine(replacement, line.Markup);
                var was = View(line, row, line.Id, cost);
                var willBe = View(newLine, replacement, null, cost);
                if (was with { RevisionRowId = willBe.RevisionRowId, LineId = null } != willBe)
                    changed.Add(new(was, willBe));
                continue;
            }

            var beforeGroup = old.Select(x => View(x.Line, x.Row, x.Line.Id, cost)).ToList();
            var afterGroup = group.After.Select(r => View(BuildLine(r, null), r, null, cost)).ToList();
            if (beforeGroup.Count == 0) added.AddRange(afterGroup);
            else if (afterGroup.Count == 0) removed.AddRange(beforeGroup);
            else unmatched.Add(new(beforeGroup, afterGroup));
        }

        return new(target, lines, sections, preview with
        {
            Added = added, Removed = removed, Changed = changed, Unmatched = unmatched,
            UnmatchedSectionMarkups = unmatchedSectionMarkups,
        });

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

    private static (Dictionary<string, decimal> Matched, List<OfferRebaseSectionMarkupDto> Unmatched) SectionMarkups(
        Offer offer, IReadOnlyList<RebaseSections> groups)
    {
        var old = groups.SelectMany(g => g.Before.Select(s => (Section: s, Group: g)))
            .Where(x => x.Section.Code is not null).ToDictionary(x => x.Section.Code!, StringComparer.Ordinal);
        var matched = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var unmatched = new List<OfferRebaseSectionMarkupDto>();
        foreach (var markup in offer.SectionMarkups.OrderBy(s => s.SectionCode, StringComparer.Ordinal))
        {
            var (from, group) = old.GetValueOrDefault(markup.SectionCode);
            if (group is { Paired: true } && group.After[0].Code is { } code)
                matched.Add(code, markup.Markup);
            else
                unmatched.Add(new(new(markup.SectionCode, from?.Name), markup.Markup,
                    group?.After.Select(s => new LsrSectionDto(s.Code, s.Name)).ToList() ?? []));
        }
        return (matched, unmatched);
    }

    private static OfferRebaseLineDto View(OfferLine line, EstimateRow row, Guid? lineId, bool cost) => new(
        row.Id, lineId, line.Number,
        line.LsrSectionCode is null && line.LsrSectionName is null ? null : new(line.LsrSectionCode, line.LsrSectionName),
        line.Name, line.Volume, line.Unit, line.WorkTypeId, line.Material ? "material" : "work",
        line.Markup, line.Price, line.Amount,
        cost ? new(row.Total, row.Total is { } basis ? line.Amount - basis : null) : null);
}
