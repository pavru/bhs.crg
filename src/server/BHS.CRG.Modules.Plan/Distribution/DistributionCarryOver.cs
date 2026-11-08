using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Distribution;

/// <summary>Чем кончился перенос: источник и числа по категориям — для ответа создания редакции и журнала.</summary>
public sealed record CarryOverSummary(
    Guid? SourceRevisionId, int SourceNumber, int Carried, int Shifted, int Ambiguous, int Overflow, int Lost)
{
    public CarryOverCountsDto Dto() => new(SourceRevisionId, SourceNumber, Carried, Shifted, Ambiguous, Overflow, Lost);

    public string Text() =>
        $"из ред. {SourceNumber}: перенесено {Carried}, проверить {Shifted}, неоднозначно {Ambiguous}, " +
        $"не поместилось {Overflow}, не перенесено {Lost}";
}

/// <summary>
/// Перенос распределения при повторном импорте — шаг создания редакции (change <c>plan-10-distribution</c>,
/// задача 1.5, шаг <c>plan-07</c>): распределение источника кладётся на строки новой редакции в той же фиксации,
/// что и сама редакция, — редакции без перенесённого или перенесённого без редакции не бывает.
///
/// <para><b>Источник</b> — прежняя редакция сметы с самым поздним изменением распределения, не обязательно
/// действующая: распределять можно и не действующую, и работа в ней не должна пропадать молча. Ни у одной нет
/// распределения — переносить нечего. Сопоставление — <see cref="DistributionCarryOverPlan" />; отчёт хранится у
/// новой редакции, пока по каждой позиции не решили.</para>
///
/// <para>⚠️ Строки ДВУХ редакций одной сметы — не план стройки (<see cref="PlanRows.Current" />).</para>
/// </summary>
public sealed class DistributionCarryOver(PlanDbContext db, IModuleUser user, IModuleActivityLog log)
{
    /// <summary>Положить перенос в контекст, НЕ сохраняя; <c>null</c> — переносить нечего.</summary>
    /// <param name="revision">Новая редакция — её строки уже в контексте (<see cref="EstimateRevisionWriter.StageAsync" />).</param>
    public async Task<CarryOverSummary?> StageAsync(Estimate estimate, EstimateRevision revision, CancellationToken ct)
    {
        // Смета на разделе стройки не распределяется: её строки целиком в разделе (задача 1.9).
        if (estimate.SectionId is not null) return null;
        var head = await db.RevisionDistributions.AsNoTracking()
            .Where(h => h.EstimateId == estimate.Id && h.RevisionId != revision.Id)
            .OrderByDescending(h => h.ChangedAt).FirstOrDefaultAsync(ct);
        if (head is null) return null;

        var from = await db.EstimateRevisions.FirstAsync(r => r.Id == head.RevisionId, ct);
        var sourceRows = await db.EstimateRows.AsNoTracking().Where(r => r.RevisionId == from.Id).ToListAsync(ct);
        var rules = await db.DistributionRules.AsNoTracking().Where(r => r.RevisionId == from.Id).ToListAsync(ct);
        var parts = await db.DistributionParts.AsNoTracking().Where(p => p.RevisionId == from.Id).ToListAsync(ct);
        if (rules.Count == 0 && parts.Count == 0) return null;

        var target = db.ChangeTracker.Entries<EstimateRow>()
            .Where(e => e.State == EntityState.Added && e.Entity.RevisionId == revision.Id)
            .Select(e => CarryRow.Of(e.Entity)).ToList();
        var plan = DistributionCarryOverPlan.Build(new CarrySource(
                [.. sourceRows.Select(CarryRow.Of)],
                rules.ToDictionary(r => new LsrKey(r.LsrSectionCode), r => r.Target),
                parts.GroupBy(p => p.RowId).ToDictionary(g => g.Key,
                    g => (IReadOnlyList<CarriedPart>)[.. g.OrderByDescending(p => p.Quantity).Select(p => new CarriedPart(p.SectionId, p.Quantity))])),
            target);

        var created = RevisionDistribution.Create(estimate, revision);
        created.Touch(estimate, revision, user.Id, user.Name, DateTimeOffset.UtcNow);
        created.CarriedFrom(from, plan.Carried);
        db.RevisionDistributions.Add(created);
        foreach (var (section, rule) in plan.Rules)
            db.DistributionRules.Add(DistributionRule.Create(revision.Id, section.Code, rule));
        foreach (var (rowId, own) in plan.Parts)
        foreach (var part in own)
            db.DistributionParts.Add(DistributionPart.Create(revision.Id, rowId, part.SectionId, part.Quantity));
        foreach (var item in plan.Items)
            db.CarryOverItems.Add(CarryOverItem.Create(revision.Id, item.Category, item.Subject, item.Source, item.Parts,
                item.Rule, item.RowId, item.NewVolume, item.Candidates));

        return new CarryOverSummary(from.Id, from.Number, plan.Carried, plan.Count(CarryOverCategory.Shifted),
            plan.Count(CarryOverCategory.Ambiguous), plan.Count(CarryOverCategory.Overflow), plan.Count(CarryOverCategory.Lost));
    }

    /// <summary>Перенос записан — след в журнале (после фиксации, как всякая запись журнала модуля).</summary>
    public Task RecordAsync(Estimate estimate, EstimateRevision revision, CarryOverSummary summary, CancellationToken ct) =>
        log.RecordAsync(EstimateActions.DistributionCarryOver, revision.Id.ToString(),
            $"{EstimateActions.Label(estimate)}, ред. {revision.Number}", after: summary.Text(), ct: ct);

    /// <summary>Перенос уже созданной редакции — из её отчёта: повтор создания отвечает теми же числами.</summary>
    public async Task<CarryOverSummary?> StoredAsync(Guid revisionId, CancellationToken ct)
    {
        var head = await db.RevisionDistributions.AsNoTracking().FirstOrDefaultAsync(h => h.RevisionId == revisionId, ct);
        if (head?.CarriedFromNumber is not { } number) return null;
        var counts = await db.CarryOverItems.AsNoTracking().Where(i => i.RevisionId == revisionId)
            .GroupBy(i => i.Category).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return new CarryOverSummary(head.CarriedFromRevisionId, number, head.CarriedRows,
            counts.GetValueOrDefault(CarryOverCategory.Shifted), counts.GetValueOrDefault(CarryOverCategory.Ambiguous),
            counts.GetValueOrDefault(CarryOverCategory.Overflow), counts.GetValueOrDefault(CarryOverCategory.Lost));
    }
}
