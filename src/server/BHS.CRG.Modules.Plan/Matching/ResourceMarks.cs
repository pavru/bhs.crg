using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Matching;

/// <summary>
/// Пометка материала «запомнено» (change <c>plan-08-matching</c>, D17 зонтика): материал сопоставило
/// запомненное, и текст говорит, что с ним стало с тех пор (забыли, перенацелили). Вычисляется на чтении, как
/// у строк (<see cref="RateMarks" />): «забыть» в списке не обходит материалы всех смет.
/// </summary>
internal sealed class ResourceMarks
{
    private readonly Dictionary<Guid, ResourceMatch> byId;

    private ResourceMarks(Dictionary<Guid, ResourceMatch> byId) => this.byId = byId;

    public static async Task<ResourceMarks> ForAsync(
        PlanDbContext db, IEnumerable<EstimateRowMaterial> materials, CancellationToken ct)
    {
        var ids = materials.Select(m => m.ResourceMatchId).OfType<Guid>().Distinct().ToList();
        return new(ids.Count == 0
            ? []
            : await db.ResourceMatches.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct));
    }

    public IReadOnlyList<PlanMark> Of(EstimateRowMaterial material)
    {
        if (material.ResourceMatchId is not { } id) return [];
        if (!byId.TryGetValue(id, out var match))
            return [new(RateMarks.Remembered, "Подставлено из запомненного; с тех пор его забыли — материал остался как был.")];

        var said = match.Kind == ResourceMatchKind.Code
            ? $"Подставлено из запомненного по коду «{match.SourceText}»"
            : "Подставлено из запомненного по наименованию";
        return
        [
            new(RateMarks.Remembered, match.NomenclatureId != material.NomenclatureId
                ? $"{said}; с тех пор его перенацелили — материал остался как был."
                : $"{said}. Не то — сопоставьте материал заново."),
        ];
    }
}
