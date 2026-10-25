using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Matching;

/// <summary>
/// Пометки строк о запомненном (change <c>plan-08-matching</c>, D17 зонтика): кодом и текстом, с сервера.
///
/// <list type="bullet">
///   <item><c>remembered</c> — строку сопоставило запомненное; текст говорит и то, что с ним стало с тех пор
///   (забыли, перенацелили): правка запомненного строки чужих редакций не переписывает, и узнать о ней строка
///   может только так;</item>
///   <item><c>unit-changed</c> — шифр знаком, но запомнен для другой единицы расценки, и потому не подставлен.</item>
/// </list>
///
/// <para>Обе ВЫЧИСЛЯЮТСЯ на чтении, а не хранятся: «забыть» в списке не обязано обходить строки всех смет, а
/// пометка «единица изменилась» живёт ровно пока строка ждёт выбора.</para>
/// </summary>
internal sealed class RateMarks
{
    public const string Remembered = "remembered";
    public const string UnitChanged = "unit-changed";

    private readonly Dictionary<Guid, RateMatch> byId;
    private readonly Dictionary<string, RateMatch> byKey;
    private readonly string? normativeBase;

    private RateMarks(Dictionary<Guid, RateMatch> byId, Dictionary<string, RateMatch> byKey, string? normativeBase) =>
        (this.byId, this.byKey, this.normativeBase) = (byId, byKey, normativeBase);

    public static async Task<RateMarks> ForAsync(
        PlanDbContext db, EstimateRevision revision, IReadOnlyList<EstimateRow> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.RateMatchId).OfType<Guid>().Distinct().ToList();
        var byId = ids.Count == 0
            ? []
            : await db.RateMatches.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var waiting = rows.Where(r => r.Unmatched).Select(r => RateMatchKey.Of(r.RateCode, revision.NormativeBase)).OfType<RateMatchKey>();
        return new(byId, await RateMatching.FindAsync(db, waiting, ct), revision.NormativeBase);
    }

    public IReadOnlyList<PlanMark> Of(EstimateRow row)
    {
        if (row.RateMatchId is { } id)
        {
            var said = $"Подставлено из запомненного по шифру «{row.RateCode}»";
            return
            [
                new(Remembered, !byId.TryGetValue(id, out var match)
                    ? $"{said}; с тех пор его забыли — строка осталась как была."
                    : match.WorkTypeId != row.WorkTypeId || match.UnitId != row.UnitId || match.Factor != row.Factor
                        ? $"{said}; с тех пор его перенацелили — строка осталась как была."
                        : $"{said}. Не то — сопоставьте строку заново."),
            ];
        }

        if (row.Unmatched && RateMatchKey.Of(row.RateCode, normativeBase) is { } key
            && byKey.TryGetValue(key.Hash, out var known) && !RateMatching.SameUnit(known.SourceUnit, row.SourceUnit))
            return
            [
                new(UnitChanged,
                    $"Шифр «{row.RateCode}» запомнен для единицы «{known.SourceUnit}», а в файле — «{row.SourceUnit}»: " +
                    "запомненное не подставлено, коэффициент считался от другой единицы."),
            ];
        return [];
    }
}
