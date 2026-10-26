using System.Globalization;
using System.Text.Json.Serialization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Предпросмотр смены действующей редакции (change <c>plan-31-estimate-ui-carriers</c>, D7, ТЗ PLAN-5.2): что
/// станет с планом, если сделать редакцию действующей, — без правки. Смена по-прежнему одна —
/// <c>POST …/current-revision</c> с <c>If-Match</c> из этого ответа: смету тем временем изменили — 409 там.
///
/// <para><b>Только изменившееся</b> (решение владельца, П1): объёмы по виду работы и единице «было → станет»
/// там, где они разные, — строки сметы складываются тем же правилом, что план стройки (<see cref="PlanRows" />):
/// сопоставленные строки-работы. Деньги «было → станет» — итог редакции, как в ответе сметы, и только с
/// <c>plan.estimate.cost</c>.</para>
///
/// <para>⚠️ Суммы ДВУХ редакций ОДНОЙ сметы для показа, а не план стройки: тот складывает все сметы и читается
/// только <see cref="PlanRows.Current" />.</para>
/// </summary>
internal static class CurrentRevisionPreview
{
    public static async Task<IResult> GetAsync(
        Guid id, Guid? revisionId, PlanDbContext db, IModuleUser user, CancellationToken ct)
    {
        if (revisionId is not { } target)
            throw new InvalidRequestException("Не названа редакция (revisionId), которую предлагают сделать действующей.");
        var found = await db.Estimates.AsNoTracking().Where(e => e.Id == id)
            .Select(e => new { e.CurrentRevisionId, Version = EF.Property<uint>(e, PlanDbContext.RowVersion) })
            .FirstOrDefaultAsync(ct) ?? throw EstimateViews.NotFound();
        var revisions = await db.EstimateRevisions.AsNoTracking()
            .Where(r => r.EstimateId == id && (r.Id == target || r.Id == found.CurrentRevisionId))
            .Select(r => new { r.Id, r.Number })
            .ToListAsync(ct);
        var number = revisions.FirstOrDefault(r => r.Id == target)?.Number
            ?? throw new InvalidRequestException("Редакции нет — её удалили либо она не этой сметы.");
        var current = found.CurrentRevisionId;
        List<Guid> ids = current is { } was && was != target ? [target, was] : [target];

        var volumes = await db.EstimateRows.AsNoTracking()
            .Where(r => ids.Contains(r.RevisionId) && r.Kind == EstimateRowKind.Work && r.WorkTypeId != null && r.PlanItemId != null)
            .GroupBy(r => new { r.RevisionId, r.WorkTypeId, r.UnitId })
            .Select(g => new { g.Key.RevisionId, WorkTypeId = g.Key.WorkTypeId!.Value, g.Key.UnitId, Volume = g.Sum(r => r.Volume ?? 0m) })
            .ToListAsync(ct);
        decimal VolumeOf(Guid? revision, Guid workType, Guid? unit) =>
            revision is null ? 0m
            : volumes.Where(v => v.RevisionId == revision && v.WorkTypeId == workType && v.UnitId == unit).Sum(v => v.Volume);
        var changes = volumes.Select(v => (v.WorkTypeId, v.UnitId)).Distinct()
            .Select(k => new PlanVolumeChangeDto(k.WorkTypeId, k.UnitId, VolumeOf(current, k.WorkTypeId, k.UnitId),
                VolumeOf(target, k.WorkTypeId, k.UnitId)))
            .Where(c => c.Was != c.Becomes)
            .OrderBy(c => c.WorkTypeId).ThenBy(c => c.UnitId)
            .ToList();

        var unmatched = await db.EstimateRows.AsNoTracking().CountAsync(r => r.RevisionId == target
            && r.Kind == EstimateRowKind.Work && (r.WorkTypeId == null || r.PlanItemId == null), ct);

        var hidden = await EstimateViews.HiddenAsync(user, ct);
        PreviewCostDto? cost = null;
        if (hidden is null)
        {
            var totals = await db.EstimateRows.AsNoTracking().Where(r => ids.Contains(r.RevisionId))
                .GroupBy(r => r.RevisionId).Select(g => new { g.Key, Total = g.Sum(r => r.Total ?? 0m) })
                .ToDictionaryAsync(t => t.Key, t => t.Total, ct);
            cost = new PreviewCostDto(current is { } c ? totals.GetValueOrDefault(c) : 0m, totals.GetValueOrDefault(target));
        }

        return Results.Ok(new CurrentRevisionPreviewDto(
            target, number, revisions.FirstOrDefault(r => r.Id == current)?.Number, changes, unmatched,
            found.Version.ToString(CultureInfo.InvariantCulture), cost, hidden));
    }
}

/// <param name="Was">Объём плана по смете сейчас — 0, если вида работы в действующей нет.</param>
/// <param name="Becomes">Объём после смены.</param>
public sealed record PlanVolumeChangeDto(Guid WorkTypeId, Guid? UnitId, decimal Was, decimal Becomes);

/// <summary>Итог действующей и предлагаемой редакций — только с <c>plan.estimate.cost</c>.</summary>
public sealed record PreviewCostDto(decimal Was, decimal Becomes);

/// <param name="CurrentRevisionNumber">Номер действующей сейчас; <c>null</c> — действующей нет.</param>
/// <param name="Changes">Только изменившиеся объёмы плана по виду работы и единице.</param>
/// <param name="Unmatched">Строк-работ предлагаемой редакции без вида работы или позиции перечня — в план не войдут.</param>
/// <param name="Version">Версия сметы — её называет <c>POST …/current-revision</c> в <c>If-Match</c>.</param>
public sealed record CurrentRevisionPreviewDto(
    Guid RevisionId, int RevisionNumber, int? CurrentRevisionNumber, IReadOnlyList<PlanVolumeChangeDto> Changes,
    int Unmatched, string Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreviewCostDto? Cost,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HiddenPart>? Hidden);
