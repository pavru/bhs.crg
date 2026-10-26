using System.Globalization;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Matching;
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

        // Итог редакции — только тому, кто его увидит: без права сумма по строкам не считается вовсе.
        // «Не сопоставлена» — только строка-работа (D2 plan-31): то же правило, что EstimateRow.Unmatched.
        var rows = db.EstimateRows.AsNoTracking().Where(row => revisionIds.Contains(row.RevisionId));
        var stats = await (cost
                ? rows.GroupBy(row => row.RevisionId).Select(g => new RevisionStats(
                    g.Key, g.Count(),
                    g.Count(x => x.Kind == EstimateRowKind.Work && (x.WorkTypeId == null || x.PlanItemId == null)),
                    g.Sum(x => x.Total ?? 0m)))
                : rows.GroupBy(row => row.RevisionId).Select(g => new RevisionStats(
                    g.Key, g.Count(),
                    g.Count(x => x.Kind == EstimateRowKind.Work && (x.WorkTypeId == null || x.PlanItemId == null)), null)))
            .ToDictionaryAsync(s => s.Key, ct);
        var materials = await (
                from m in db.EstimateRowMaterials.AsNoTracking()
                join row in rows on m.RowId equals row.Id
                where m.NomenclatureId == null
                group m by row.RevisionId into g
                select new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var unitChanged = await RateMarks.UnitChangedAsync(
            db, revisions.ToDictionary(r => r.Revision.Id, r => r.Revision.NormativeBase), ct);
        // Импорт, из которого редакция создана, — им называется и «частично N из M» (D4 plan-31).
        var imports = await db.EstimateImports.AsNoTracking()
            .Where(i => i.RevisionId != null && revisionIds.Contains(i.RevisionId.Value))
            .Select(i => new { RevisionId = i.RevisionId!.Value, i.Id, i.ParsedPositions, i.PositionsInFile })
            .ToDictionaryAsync(i => i.RevisionId, ct);

        return [.. heads.Select(h =>
        {
            var own = revisions.Where(r => r.Revision.EstimateId == h.Estimate.Id).Select(r =>
            {
                var s = stats.GetValueOrDefault(r.Revision.Id);
                var frozen = r.Revision.FrozenAt is { } at
                    ? new EstimateFrozenDto(at, r.Revision.FrozenCode!, r.Revision.FrozenLabel!)
                    : null;
                var import = imports.GetValueOrDefault(r.Revision.Id);
                var partial = import is { ParsedPositions: { } parsed, PositionsInFile: { } inFile } && parsed < inFile
                    ? new RevisionPartialDto(parsed, inFile)
                    : null;
                return new EstimateRevisionDto(
                    r.Revision.Id, r.Revision.Number, r.Revision.Id == h.Estimate.CurrentRevisionId,
                    r.Revision.ImportedAt, r.Revision.ImportedByName, r.Revision.SourceFileName,
                    r.Revision.SchemaVersion, s?.Rows ?? 0, s?.Unmatched ?? 0,
                    materials.GetValueOrDefault(r.Revision.Id), unitChanged.GetValueOrDefault(r.Revision.Id),
                    import?.Id, partial, frozen, Text(r.Version),
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

    /// <param name="Total">Итог по строкам; <c>null</c> — не считался: у читающего нет права.</param>
    private sealed record RevisionStats(Guid Key, int Rows, int Unmatched, decimal? Total);
}
