using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Import;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Plan.Matching;

/// <summary>
/// Подстановка запомненного при создании редакции из импорта (change <c>plan-08-matching</c>, ТЗ PLAN-6.1,
/// PLAN-9.1) — реализация точки <see cref="IEstimateRowMatcher" />, которую <c>plan-07</c> оставил пустой.
///
/// <para><b>Шифр.</b> Строка со знакомым шифром получает вид работы, единицу, коэффициент и объём плана — с
/// пометкой «запомнено» (<see cref="EstimateRowDraft.RateMatchId" />). Позицию перечня заводит создание
/// редакции (шаг 2): архивный вид работы или единица там получат отказ ключа, и строка ляжет несопоставленной
/// с причиной.</para>
///
/// <para>⚠️ <b>Единица расценки сверяется</b>: у знакомого шифра в новом файле другая единица — запомненное
/// не подставляется. Коэффициент считался от прежней единицы, и подставленный к новой он ошибся бы в план во
/// столько раз, во сколько единицы различаются. Строка остаётся несопоставленной, а пометку
/// <c>unit-changed</c> чтение строк вычисляет само — она живёт, пока строка ждёт выбора.</para>
///
/// <para><b>Ресурс.</b> Материал со знакомым кодом (или наименованием) получает позицию номенклатуры с
/// пометкой «запомнено» (<see cref="EstimateMaterialDraft.ResourceMatchId" />) — только ДЕЙСТВУЮЩУЮ: ссылки
/// материалов редакция сверяет до записи, и одна архивная позиция отказала бы всей редакции. Материал с
/// запомненной архивной или удалённой позицией ложится несопоставленным и ждёт выбора.</para>
/// </summary>
public sealed class RememberedRowMatcher(PlanDbContext db, IModuleCatalog catalog) : IEstimateRowMatcher
{
    public async Task<IReadOnlyList<EstimateRowDraft>> ApplyAsync(
        EstimateRowMatchTarget target, IReadOnlyList<EstimateRowDraft> rows, CancellationToken ct)
    {
        var keys = rows.Select(r => r.WorkTypeId is null ? RateMatchKey.Of(r.RateCode, target.NormativeBase) : null).ToList();
        var rates = await RateMatching.FindAsync(db, keys.OfType<RateMatchKey>(), ct);
        var resources = await ResourceMatching.FindAsync(db, rows
            .SelectMany(r => r.Materials ?? [])
            .Where(m => m.NomenclatureId is null)
            .SelectMany(m => ResourceMatchKey.Sought(m.SourceCode, m.SourceName)), ct);
        var live = await LiveAsync(resources.Values, ct);
        if (rates.Count == 0 && live.Count == 0) return rows;

        return [.. rows.Select((row, i) => Materials(Rate(row, keys[i], rates), resources, live))];
    }

    private static EstimateRowDraft Rate(EstimateRowDraft row, RateMatchKey? key, Dictionary<string, RateMatch> known) =>
        key is not null && known.TryGetValue(key.Hash, out var match) && RateMatching.SameUnit(match.SourceUnit, row.SourceUnit)
            ? row with
            {
                WorkTypeId = match.WorkTypeId,
                UnitId = match.UnitId,
                Factor = match.Factor,
                Volume = row.SourceVolume * match.Factor,
                RateMatchId = match.Id,
            }
            : row;

    private static EstimateRowDraft Materials(
        EstimateRowDraft row, IReadOnlyDictionary<(ResourceMatchKind, string), ResourceMatch> known, HashSet<Guid> live) =>
        row.Materials is not { Count: > 0 } materials || live.Count == 0
            ? row
            : row with
            {
                Materials = [.. materials.Select(m =>
                    m.NomenclatureId is null && ResourceMatching.Recall(known, m.SourceCode, m.SourceName) is { } match
                        && live.Contains(match.NomenclatureId)
                        ? m with { NomenclatureId = match.NomenclatureId, ResourceMatchId = match.Id }
                        : m)],
            };

    /// <summary>Позиции запомненного, которые сейчас можно подставить: на месте и не в архиве. Справочник не
    /// назначен — подставлять нечего, и материалы ложатся несопоставленными.</summary>
    private async Task<HashSet<Guid>> LiveAsync(IEnumerable<ResourceMatch> matches, CancellationToken ct)
    {
        var ids = matches.Select(m => m.NomenclatureId).Distinct().ToList();
        if (ids.Count == 0) return [];
        var found = await catalog.RefsAsync(FunctionalTag.RoleNomenclature, ids, ct);
        return found is null ? [] : [.. found.Where(r => !r.Archived).Select(r => r.Id)];
    }
}
