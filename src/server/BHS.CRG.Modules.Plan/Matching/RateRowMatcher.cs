using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Import;

namespace BHS.CRG.Modules.Plan.Matching;

/// <summary>
/// Подстановка запомненного при создании редакции из импорта (change <c>plan-08-matching</c>, ТЗ PLAN-6.1) —
/// реализация точки <see cref="IEstimateRowMatcher" />, которую <c>plan-07</c> оставил пустой.
///
/// <para>Строка со знакомым шифром получает вид работы, единицу, коэффициент и объём плана — с пометкой
/// «запомнено» (<see cref="EstimateRowDraft.RateMatchId" />). Позицию перечня заводит создание редакции
/// (шаг 2): архивный вид работы или единица там получат отказ ключа, и строка ляжет несопоставленной с
/// причиной.</para>
///
/// <para>⚠️ <b>Единица расценки сверяется</b>: у знакомого шифра в новом файле другая единица — запомненное
/// не подставляется. Коэффициент считался от прежней единицы, и подставленный к новой он ошибся бы в план во
/// столько раз, во сколько единицы различаются. Строка остаётся несопоставленной, а пометку
/// <c>unit-changed</c> чтение строк вычисляет само — она живёт, пока строка ждёт выбора.</para>
/// </summary>
public sealed class RateRowMatcher(PlanDbContext db) : IEstimateRowMatcher
{
    public async Task<IReadOnlyList<EstimateRowDraft>> ApplyAsync(
        EstimateRowMatchTarget target, IReadOnlyList<EstimateRowDraft> rows, CancellationToken ct)
    {
        var keys = rows.Select(r => r.WorkTypeId is null ? RateMatchKey.Of(r.RateCode, target.NormativeBase) : null).ToList();
        var known = await RateMatching.FindAsync(db, keys.OfType<RateMatchKey>(), ct);
        if (known.Count == 0) return rows;

        return [.. rows.Select((row, i) =>
            keys[i] is { } key && known.TryGetValue(key.Hash, out var match) && RateMatching.SameUnit(match.SourceUnit, row.SourceUnit)
                ? row with
                {
                    WorkTypeId = match.WorkTypeId,
                    UnitId = match.UnitId,
                    Factor = match.Factor,
                    Volume = row.SourceVolume * match.Factor,
                    RateMatchId = match.Id,
                }
                : row)];
    }
}
