using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Distribution;

/// <summary>Редакция, которую распределяют: смета, заголовок распределения (отслеживается) и разделы стройки.</summary>
/// <param name="Sections">Разделы стройки сметы; стройки в ядре нет — пусто.</param>
internal sealed record DistributionPlace(
    Estimate Estimate, EstimateRevision Revision, RevisionDistribution? Head, IReadOnlyList<ModuleSection> Sections)
{
    public string? SectionName(Guid id) => Sections.FirstOrDefault(s => s.Id == id)?.Name;

    /// <summary>Смета на разделе стройки — распределять нечего (задача 1.9).</summary>
    public EstimateOnSectionException? OnSection =>
        Estimate.SectionId is { } section ? new EstimateOnSectionException(section, SectionName(section)) : null;

    public static async Task<DistributionPlace> LoadAsync(
        PlanDbContext db, IModuleConstructions constructions, Guid revisionId, CancellationToken ct)
    {
        var revision = await db.EstimateRevisions.FirstOrDefaultAsync(r => r.Id == revisionId, ct)
            ?? throw new NotFoundException("Редакция не найдена — возможно, её удалили.");
        var estimate = await db.Estimates.FirstAsync(e => e.Id == revision.EstimateId, ct);
        var head = await db.RevisionDistributions.FirstOrDefaultAsync(h => h.RevisionId == revisionId, ct);
        var construction = (await constructions.ListAsync(ct)).FirstOrDefault(c => c.Id == estimate.ConstructionId);
        return new DistributionPlace(estimate, revision, head, construction?.Sections ?? []);
    }
}

/// <summary>
/// Распределение редакции одним ответом (change <c>plan-10-distribution</c>, задача 1.3): правила разделов ЛСР,
/// строки с частями в обеих единицах, итог по целям и отчёт переноса. Без страниц — строк в смете сотни.
///
/// <para>⚠️ Строки ОДНОЙ редакции для показа, а не план стройки — тот читается только <see cref="PlanRows.Current" />.</para>
/// </summary>
internal sealed class DistributionReader(PlanDbContext db, IModuleConstructions constructions, IModuleUnits units)
{
    public async Task<DistributionDto> ReadAsync(Guid revisionId, CancellationToken ct) =>
        await ReadAsync(await DistributionPlace.LoadAsync(db, constructions, revisionId, ct), ct);

    public async Task<DistributionDto> ReadAsync(DistributionPlace place, CancellationToken ct)
    {
        var revisionId = place.Revision.Id;
        var rows = await db.EstimateRows.AsNoTracking().Where(r => r.RevisionId == revisionId).OrderBy(r => r.Order)
            .ToListAsync(ct);
        var current = place.Estimate.CurrentRevisionId == revisionId;
        var version = db.VersionOf(place.Head);

        if (place.OnSection is { } onSection)
            return new DistributionDto(revisionId, false,
                new DistributionUnavailableDto(EstimateOnSectionException.Code, onSection.SectionId, onSection.SectionName),
                version, place.Head?.ChangedAt, place.Head?.ChangedByName, current, place.Revision.Frozen,
                new RevisionDistributionDto(rows.Count, rows.Count, 0, 0, 0, 0, place.Head?.ChangedAt),
                new DistributionTotalsDto([], 0, 0), [], [], null);

        var rules = await db.DistributionRules.AsNoTracking().Where(r => r.RevisionId == revisionId).ToListAsync(ct);
        var parts = await db.DistributionParts.AsNoTracking().Where(p => p.RevisionId == revisionId).ToListAsync(ct);
        var summary = DistributionLines.Evaluate(
            [.. rows.Select(r => new LineInput(r.Id, r.LsrSectionCode, r.SourceVolume))], rules, parts);
        var carryOver = await CarryOverAsync(place.Head, rows, ct);
        var unitNames = await UnitNamesAsync(rows, ct);

        var lines = rows.Zip(summary.Lines, (row, state) => Line(row, state, unitNames)).ToList();
        var sections = rows.GroupBy(r => new LsrKey(r.LsrSectionCode))
            .Select(g => (Key: g.Key, Name: g.First().LsrSectionName, State: summary.Sections[g.Key]))
            .Select(s => new DistributionRuleDto(s.Key.Code, s.Name, TargetOf(s.State.Rule), s.State.Rows,
                s.State.Exceptions, s.State.Undistributed))
            .ToList();

        return new DistributionDto(revisionId, true, null, version, place.Head?.ChangedAt, place.Head?.ChangedByName,
            current, place.Revision.Frozen,
            Counts(summary.Tally, carryOver?.Pending ?? 0, place.Head?.ChangedAt),
            Totals(place, summary, rules), sections, lines, carryOver);
    }

    public static RevisionDistributionDto Counts(DistributionTally t, int pending, DateTimeOffset? changedAt) =>
        new(t.Rows, t.Distributed, t.Partial, t.Whole, t.Undistributed, pending, changedAt);

    public static DistributionTargetDto? TargetOf(DistributionTarget? target) =>
        target is null ? null : new DistributionTargetDto(DistributionTarget.Code(target.Kind), target.SectionId);

    /// <summary>Итог по целям: разделы стройки по порядку ядра, затем цели, которых в стройке уже нет.</summary>
    private static DistributionTotalsDto Totals(
        DistributionPlace place, DistributionSummary summary, IEnumerable<DistributionRule> rules)
    {
        var rowsOf = summary.Lines.SelectMany(l => l.Targets).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
        var known = place.Sections.Select(s => s.Id).ToHashSet();
        var lost = rowsOf.Keys.Concat(rules.Select(r => r.SectionId).OfType<Guid>())
            .Where(id => !known.Contains(id)).Distinct();
        return new DistributionTotalsDto(
            [
                .. place.Sections.Select(s => new DistributionSectionTotalDto(s.Id, s.Name, false, rowsOf.GetValueOrDefault(s.Id))),
                .. lost.Select(id => new DistributionSectionTotalDto(id, null, true, rowsOf.GetValueOrDefault(id))),
            ],
            summary.Lines.Count(l => l.Whole), summary.Tally.Undistributed);
    }

    private static DistributionLineDto Line(EstimateRow row, LineState state, IReadOnlyDictionary<Guid, string> unitNames)
    {
        var facts = LineFacts.Of(row);
        QuantityDto Both(decimal quantity) => new(quantity, DistributionMath.ToPlan(quantity, row.Factor));
        return new DistributionLineDto(
            row.Id, row.Order, row.LsrSectionCode, facts.Number, row.Name, row.IsMaterial ? "material" : "work",
            row.Subordinate, Code(state.Source), Code(state.Status), facts.Splittable,
            facts.Splittable ? null : "no-volume",
            row.SourceVolume is { } volume ? new QuantityDto(volume, row.Volume) : null,
            new LineUnitsDto(row.SourceUnit, row.UnitId is { } unit ? unitNames.GetValueOrDefault(unit) : null),
            [.. state.Parts.Select(p => new DistributionPartDto(p.SectionId, Both(p.Quantity)))],
            state.Rest is { } rest ? Both(rest) : null, state.Targets, state.Whole);
    }

    /// <summary>Отчёт переноса: числа по категориям за всё время и позиции, по которым ещё не решили.</summary>
    private async Task<CarryOverDto?> CarryOverAsync(RevisionDistribution? head, IReadOnlyList<EstimateRow> rows, CancellationToken ct)
    {
        if (head?.CarriedFromNumber is null) return null;
        var items = await db.CarryOverItems.AsNoTracking().Where(i => i.RevisionId == head.RevisionId).ToListAsync(ct);
        var factors = rows.ToDictionary(r => r.Id, r => r.Factor);
        int Of(CarryOverCategory category) => items.Count(i => i.Category == category);
        var open = items.Where(i => i.Resolution is null)
            .OrderBy(i => i.Pending ? 0 : 1).ThenBy(i => i.LsrSectionCode).ThenBy(i => i.Number)
            .Select(i =>
            {
                var factor = i.RowId is { } own ? factors.GetValueOrDefault(own) : null;
                return new CarryOverItemDto(i.Id, CategoryCode(i.Category), i.Subject == CarryOverSubject.Rule ? "rule" : "row",
                    i.Pending, i.LsrSectionCode, i.LsrSectionName, i.RateCode, i.NumberInSection, i.Number, i.Name,
                    i.SourceVolume, i.SourceUnit,
                    [.. i.PartList.Select(p => new DistributionPartDto(p.SectionId,
                        new QuantityDto(p.Quantity, DistributionMath.ToPlan(p.Quantity, factor))))],
                    i.RuleKind is { } kind ? TargetOf(new DistributionTarget(kind, i.RuleSectionId)) : null,
                    i.RowId, i.NewVolume, i.Candidates);
            })
            .ToList();
        return new CarryOverDto(
            new CarryOverCountsDto(head.CarriedFromRevisionId, head.CarriedFromNumber, head.CarriedRows,
                Of(CarryOverCategory.Shifted), Of(CarryOverCategory.Ambiguous), Of(CarryOverCategory.Overflow),
                Of(CarryOverCategory.Lost)),
            open.Count(i => i.Pending), open);
    }

    private async Task<IReadOnlyDictionary<Guid, string>> UnitNamesAsync(IEnumerable<EstimateRow> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.UnitId).OfType<Guid>().ToHashSet();
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        // Справочник единиц не настроен — имена не показываются, а не отказ: распределение от них не зависит.
        return (await units.GetAsync(ids, ct))?.ToDictionary(u => u.Id, u => u.Name) ?? new Dictionary<Guid, string>();
    }

    public static string CategoryCode(CarryOverCategory category) => category switch
    {
        CarryOverCategory.Shifted => "shifted",
        CarryOverCategory.Ambiguous => "ambiguous",
        CarryOverCategory.Overflow => "overflow",
        _ => "lost",
    };

    private static string Code(LineSource source) => source switch
    {
        LineSource.Parts => "parts",
        LineSource.Rule => "rule",
        _ => "none",
    };

    private static string Code(LineStatus status) => status switch
    {
        LineStatus.Distributed => "distributed",
        LineStatus.Partial => "partial",
        LineStatus.Whole => "whole",
        _ => "undistributed",
    };
}
