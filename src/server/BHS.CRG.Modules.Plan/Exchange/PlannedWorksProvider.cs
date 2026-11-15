using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Distribution;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Exchange;

/// <summary>
/// Поставщик контракта «Плановые работы» (change <c>plan-23-planned-works-contract</c>, ТЗ CORE-34, PLAN-9.1,
/// PLAN-23): план стройки — для модулей, которым схема <c>plan</c> закрыта.
///
/// <para><b>Строки — только <see cref="PlanRows" /></b>: действующие редакции всех смет стройки,
/// сопоставленные строки-работы; строки-материалы и число несопоставленных — его же методами, тем же
/// правилом действующих редакций. Своего пути к строкам у поставщика нет: прямое чтение удвоило бы план
/// прежней редакцией (D3 зонтика <c>plan-00-epic</c>), и его ловит <c>PlanRowsInventoryTests</c>.</para>
///
/// <para><b>Части по праву.</b> Часть без права не читается вовсе — ни запросом, ни в памяти: «скрыто» нельзя
/// выдать по ошибке тем, чего не загружали. «Скрыто» у части, которую открывает и узкое право, называет
/// узкое — его выдают роли без смет (<c>plan-26</c>, <c>work-26</c>, <c>work-28</c> ждут именно его).</para>
///
/// <para><b>Распределение</b> — правилом <c>plan-10</c> (<see cref="DistributionLines" />); смета, составленная
/// на раздел стройки, уходит в него целиком.</para>
/// </summary>
internal sealed class PlannedWorksProvider(PlanDbContext db) : IPlannedWorks
{
    public async Task<ContractAnswer<PlannedWorksAnswer>> ReadAsync(
        ModuleReader reader, PlannedWorksQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(query);
        // Система — отказом целиком: «под правами системы» стало бы обходом прав (plan-22).
        if (reader.IsSystem)
            return ContractAnswer.SystemRefused<PlannedWorksAnswer>(reader.SystemReason ?? "причина не названа");

        var volumesRefusal = ContractAnswer.Refusal<PlannedVolumes>(reader, PlanModule.EstimateRead);
        var costRefusal = ContractAnswer.Refusal<IReadOnlyList<PlannedCost>>(reader, PlanModule.EstimateCost);
        var materialsRefusal = ContractAnswer.Refusal<PlannedMaterials>(
            reader, PlanModule.EstimateMaterials, PlanModule.EstimateRead);
        var laborRefusal = ContractAnswer.Refusal<IReadOnlyList<PlannedLabor>>(
            reader, PlanModule.LaborRead, PlanModule.EstimateRead);
        var hintRefusal = ContractAnswer.Refusal<IReadOnlyList<PlannedHint>>(
            reader, PlanModule.EstimateHint, PlanModule.EstimateRead);

        var construction = query.ConstructionId;
        var unmapped = await PlanRows.Unmatched(db, construction).CountAsync(ct);

        var needsWork = volumesRefusal is null || costRefusal is null || materialsRefusal is null
                        || laborRefusal is null || hintRefusal is null;
        // Строки-материалы позиции не имеют: в объёмы и нормы они не входят, а отбор по позициям их не берёт.
        var needsMaterialRows = query.PlanItemIds is null
                                && (costRefusal is null || materialsRefusal is null || hintRefusal is null);

        var estimates = needsWork ? await CurrentEstimatesAsync(construction, ct) : [];
        var rows = needsWork ? await WorkRowsAsync(query, ct) : [];
        if (needsMaterialRows) rows = [.. rows, .. await MaterialRowsAsync(construction, ct)];

        var shares = await SharesAsync(rows, estimates, ct);
        if (query.SectionId is { } section) rows = [.. rows.Where(r => shares[r.Id].Weight(section) > 0m)];

        var rowIds = rows.Select(r => r.Id).ToArray();
        var materials = costRefusal is null || materialsRefusal is null || hintRefusal is null
            ? await MaterialsAsync(rowIds, ct) : [];
        var labor = laborRefusal is null ? await LaborAsync(rowIds, ct) : [];
        var workRows = rows.Where(r => r.PlanItemId is not null).ToList();

        return ContractAnswer.Shown(new PlannedWorksAnswer(
            volumesRefusal ?? ContractAnswer.Shown(new PlannedVolumes(
                PlannedWorksMath.Items(workRows, shares, query.SectionId),
                [.. estimates.Select(e => new PlannedBasis(e.EstimateId, e.Name, e.RevisionId, e.Number))])),
            costRefusal ?? ContractAnswer.Shown(PlannedWorksMath.Cost(rows, materials, shares, query.SectionId)),
            materialsRefusal ?? ContractAnswer.Shown(PlannedWorksMath.Materials(rows, materials, shares, query.SectionId)),
            laborRefusal ?? ContractAnswer.Shown(PlannedWorksMath.Labor(workRows, labor, shares, query.SectionId)),
            hintRefusal ?? ContractAnswer.Shown(PlannedWorksMath.Hint(rows, materials)),
            unmapped));
    }

    /// <summary>Смета стройки с действующей редакцией — основание плана.</summary>
    private sealed record CurrentEstimate(Guid EstimateId, string Name, Guid? SectionId, Guid RevisionId, int Number);

    private async Task<List<CurrentEstimate>> CurrentEstimatesAsync(Guid construction, CancellationToken ct) =>
        await (from estimate in db.Estimates.AsNoTracking()
               join revision in db.EstimateRevisions.AsNoTracking() on estimate.CurrentRevisionId equals (Guid?)revision.Id
               where estimate.ConstructionId == construction
               orderby estimate.CreatedAt, estimate.Name
               select new CurrentEstimate(estimate.Id, estimate.Name, estimate.SectionId, revision.Id, revision.Number))
            .ToListAsync(ct);

    private async Task<List<PlannedRow>> WorkRowsAsync(PlannedWorksQuery query, CancellationToken ct)
    {
        var rows = PlanRows.Current(db, query.ConstructionId).AsNoTracking();
        if (query.PlanItemIds is { } items)
        {
            var ids = items.Distinct().ToArray();
            rows = rows.Where(r => ids.Contains(r.PlanItemId!.Value));
        }
        return await Project(rows).ToListAsync(ct);
    }

    private async Task<List<PlannedRow>> MaterialRowsAsync(Guid construction, CancellationToken ct) =>
        await Project(PlanRows.CurrentMaterialRows(db, construction).AsNoTracking()).ToListAsync(ct);

    private static IQueryable<PlannedRow> Project(IQueryable<EstimateRow> rows) =>
        rows.Select(r => new PlannedRow(r.Id, r.RevisionId, r.PlanItemId, r.UnitId, r.Volume, r.SourceVolume,
            r.LsrSectionCode, r.Total));

    /// <summary>
    /// Доли строк по разделам стройки. Смета на разделе — целиком в раздел (распределения у неё нет, <c>plan-10</c>);
    /// у сметы на стройку — части строки, иначе правило её раздела ЛСР, иначе «не распределено».
    /// </summary>
    private async Task<Dictionary<Guid, RowShare>> SharesAsync(
        IReadOnlyList<PlannedRow> rows, IReadOnlyList<CurrentEstimate> estimates, CancellationToken ct)
    {
        var shares = new Dictionary<Guid, RowShare>(rows.Count);
        if (rows.Count == 0) return shares;

        var onSection = estimates.Where(e => e.SectionId is not null).ToDictionary(e => e.RevisionId, e => e.SectionId!.Value);
        var distributed = estimates.Where(e => e.SectionId is null).Select(e => e.RevisionId).ToArray();
        var rules = await db.DistributionRules.AsNoTracking().Where(r => distributed.Contains(r.RevisionId)).ToListAsync(ct);
        var parts = await db.DistributionParts.AsNoTracking().Where(p => distributed.Contains(p.RevisionId)).ToListAsync(ct);

        foreach (var revision in rows.GroupBy(r => r.RevisionId))
        {
            if (onSection.TryGetValue(revision.Key, out var section))
            {
                foreach (var row in revision) shares[row.Id] = RowShare.OnSection(section);
                continue;
            }

            var list = revision.ToList();
            var summary = DistributionLines.Evaluate(
                [.. list.Select(r => new LineInput(r.Id, r.LsrSectionCode, r.SourceVolume))],
                rules.Where(r => r.RevisionId == revision.Key), parts.Where(p => p.RevisionId == revision.Key));
            foreach (var (row, line) in list.Zip(summary.Lines))
                shares[row.Id] = RowShare.Of(line, row.SourceVolume);
        }
        return shares;
    }

    private async Task<List<PlannedRowMaterial>> MaterialsAsync(Guid[] rowIds, CancellationToken ct) =>
        rowIds.Length == 0 ? []
        : await db.EstimateRowMaterials.AsNoTracking().Where(m => rowIds.Contains(m.RowId))
            .OrderBy(m => m.RowId).ThenBy(m => m.Order)
            .Select(m => new PlannedRowMaterial(m.RowId, m.NomenclatureId, m.Quantity, m.SourceUnit, m.Amount))
            .ToListAsync(ct);

    private async Task<List<PlannedRowLabor>> LaborAsync(Guid[] rowIds, CancellationToken ct) =>
        rowIds.Length == 0 ? []
        : await db.EstimateRowLabor.AsNoTracking().Where(l => rowIds.Contains(l.RowId))
            .Select(l => new PlannedRowLabor(l.RowId, l.ManHours))
            .ToListAsync(ct);
}
