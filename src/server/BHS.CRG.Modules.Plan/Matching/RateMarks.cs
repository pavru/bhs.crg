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

    /// <summary>
    /// Строк с пометкой <c>unit-changed</c> по редакциям (счётчик редакции, D4 <c>plan-31</c>) — тем же правилом,
    /// что пометка строки: несопоставленная строка-работа, шифр знаком, единица расценки другая. Строки база
    /// сводит сама — до различных пар «шифр, единица» в редакции с их числом: в смете шифр повторяется десятками,
    /// а отпечаток ключа (SHA-256) считается здесь, по одному на шифр и базу.
    /// </summary>
    public static async Task<Dictionary<Guid, int>> UnitChangedAsync(
        PlanDbContext db, IReadOnlyDictionary<Guid, string?> normativeBases, CancellationToken ct)
    {
        var ids = normativeBases.Keys.ToList();
        if (ids.Count == 0) return [];
        var groups = await db.EstimateRows.AsNoTracking()
            .Where(r => ids.Contains(r.RevisionId) && r.RateCode != null)
            .Where(EstimateRowCounts.Unmatched)
            .GroupBy(r => new { r.RevisionId, r.RateCode, r.SourceUnit })
            .Select(g => new { g.Key.RevisionId, g.Key.RateCode, g.Key.SourceUnit, Rows = g.Count() })
            .ToListAsync(ct);
        var keys = new Dictionary<(string? Code, string? Base), RateMatchKey?>();
        var waiting = groups
            .Select(g =>
            {
                var at = (g.RateCode, normativeBases[g.RevisionId]);
                if (!keys.TryGetValue(at, out var key)) keys[at] = key = RateMatchKey.Of(g.RateCode, at.Item2);
                return (g.RevisionId, g.SourceUnit, g.Rows, Key: key);
            })
            .Where(g => g.Key is not null)
            .ToList();
        var known = await RateMatching.FindAsync(db, waiting.Select(g => g.Key!), ct);
        return waiting
            .Where(g => known.TryGetValue(g.Key!.Hash, out var match) && !RateMatching.SameUnit(match.SourceUnit, g.SourceUnit))
            .GroupBy(g => g.RevisionId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Rows));
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
