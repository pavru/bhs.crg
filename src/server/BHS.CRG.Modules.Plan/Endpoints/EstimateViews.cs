using System.Globalization;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Ответы смет — одним местом на чтение и на правку: смета с редакциями, номером действующей, признаком
/// фиксации у каждой и — только с правом <c>plan.estimate.cost</c> — итогом редакции (ТЗ PLAN-1, PLAN-5,
/// PLAN-11).
///
/// <para>⚠️ Итоги и счётчики редакции считает база, а не загрузка строк: строк в редакции тысячи. Это
/// итоги ОДНОЙ редакции, а не план стройки — план читается только <see cref="PlanRows.Current" />.</para>
/// </summary>
internal static class EstimateViews
{
    /// <summary>Что скрыто от читающего без права на себестоимость; <c>null</c> — право есть.</summary>
    public static async Task<IReadOnlyList<HiddenPart>?> HiddenAsync(IModuleUser user, CancellationToken ct) =>
        await user.HasAsync(PlanModule.EstimateCost, ct) ? null : [new("cost", PlanModule.EstimateCost)];

    public static string KindCode(EstimateKind kind) => kind == EstimateKind.Additional ? "additional" : "main";

    public static async Task<List<EstimateDto>> ListAsync(
        PlanDbContext db, IQueryable<Estimate> estimates, bool cost, CancellationToken ct)
    {
        var heads = await estimates.AsNoTracking()
            // Основные — первыми: план стройки читают от основной сметы к дополнительным.
            .OrderBy(e => e.Kind == EstimateKind.Additional).ThenBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .Select(e => new { Estimate = e, Version = EF.Property<uint>(e, PlanDbContext.RowVersion) })
            .ToListAsync(ct);
        var ids = heads.Select(h => h.Estimate.Id).ToList();

        var revisions = await db.EstimateRevisions.AsNoTracking()
            .Where(r => ids.Contains(r.EstimateId))
            .OrderBy(r => r.Number)
            .Select(r => new { Revision = r, Version = EF.Property<uint>(r, PlanDbContext.RowVersion) })
            .ToListAsync(ct);
        var revisionIds = revisions.Select(r => r.Revision.Id).ToList();

        var stats = await db.EstimateRows.AsNoTracking()
            .Where(row => revisionIds.Contains(row.RevisionId))
            .GroupBy(row => row.RevisionId)
            .Select(g => new
            {
                g.Key,
                Rows = g.Count(),
                Unmatched = g.Count(x => x.WorkTypeId == null || x.PlanItemId == null),
                Total = g.Sum(x => x.Total ?? 0m),
            })
            .ToDictionaryAsync(s => s.Key, ct);

        return [.. heads.Select(h =>
        {
            var own = revisions.Where(r => r.Revision.EstimateId == h.Estimate.Id).Select(r =>
            {
                var s = stats.GetValueOrDefault(r.Revision.Id);
                var frozen = r.Revision.FrozenAt is { } at
                    ? new EstimateFrozenDto(at, r.Revision.FrozenCode!, r.Revision.FrozenLabel!)
                    : null;
                return new EstimateRevisionDto(
                    r.Revision.Id, r.Revision.Number, r.Revision.Id == h.Estimate.CurrentRevisionId,
                    r.Revision.ImportedAt, r.Revision.ImportedByName, r.Revision.SourceFileName,
                    r.Revision.SchemaVersion, s?.Rows ?? 0, s?.Unmatched ?? 0, frozen, Text(r.Version),
                    cost ? new RevisionCostDto(s?.Total ?? 0m) : null);
            }).ToList();

            return new EstimateDto(
                h.Estimate.Id, h.Estimate.ConstructionId, h.Estimate.SectionId, h.Estimate.Name,
                KindCode(h.Estimate.Kind), Text(h.Version), h.Estimate.CurrentRevisionId,
                own.FirstOrDefault(r => r.Current)?.Number, own, Hidden: null);
        })];
    }

    /// <summary>Одна смета — тем же ответом, что в списке, с носителем «скрыто» при необходимости.</summary>
    public static async Task<EstimateDto> OneAsync(PlanDbContext db, IModuleUser user, Guid id, CancellationToken ct)
    {
        var hidden = await HiddenAsync(user, ct);
        var found = await ListAsync(db, db.Estimates.Where(e => e.Id == id), hidden is null, ct);
        return found.Count == 1 ? found[0] with { Hidden = hidden } : throw NotFound();
    }

    public static Domain.Common.NotFoundException NotFound() => new("Смета не найдена — возможно, её удалили.");

    private static string Text(uint version) => version.ToString(CultureInfo.InvariantCulture);
}
