using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Ссылки строк новой редакции на записи ядра — одним местом, до записи (change
/// <c>plan-06-estimate-model</c>, ТЗ CORE-34.4, PLAN-4). Все ссылки редакции НОВЫЕ: редакция заводится
/// целиком, и правило «новая ссылка — на действующую запись» касается каждой.
///
/// <list type="bullet">
///   <item>вид работы и единица — записи своих справочников (порты <c>IModuleWorkTypes</c>,
///   <c>IModuleUnits</c>), на месте и не в архиве;</item>
///   <item>позиция перечня — этой стройки (и раздела, если смета на раздел), того же вида работы и той же
///   единицы, что строка: иначе объём строки прибавился бы к позиции чужой стройки или в чужой единице, и
///   план разошёлся бы с «Ходом работ» молча;</item>
///   <item>позиция номенклатуры материала — запись справочника НОМЕНКЛАТУРЫ (по роли, как у счетов), на
///   месте и не в архиве: запись другого вида — не номенклатура, сколько бы она ни существовала.</item>
/// </list>
///
/// <para>Отказ называет все строки разом: человеку незачем чинить файл в несколько заходов.</para>
/// </summary>
internal static class EstimateRowReferences
{
    public static async Task EnsureAsync(
        Estimate estimate, IReadOnlyList<EstimateRowDraft> rows, IModuleWorkTypes workTypes, IModuleUnits units,
        IModulePlanItems planItems, IModuleCatalog catalog, CancellationToken ct)
    {
        var numbered = rows.Select((r, i) => (Number: i + 1, Row: r)).ToList();
        var workTypeRefs = numbered.Select(x => (x.Number, x.Row.WorkTypeId)).ToList();
        var unitRefs = numbered.Select(x => (x.Number, x.Row.UnitId)).ToList();
        var materialRefs = numbered.SelectMany(x => (x.Row.Materials ?? []).Select(m => (x.Number, m.NomenclatureId))).ToList();

        var problems = new List<string>();
        if (Ids(workTypeRefs) is { Count: > 0 } workTypeIds)
            problems.AddRange(Judge(workTypeRefs, (await workTypes.GetAsync(workTypeIds, ct))?.ToDictionary(w => w.Id, w => w.Archived)
                ?? throw new InvalidRequestException(IModuleWorkTypes.NotSetUp), "вид работы"));
        if (Ids(unitRefs) is { Count: > 0 } unitIds)
            problems.AddRange(Judge(unitRefs, (await units.GetAsync(unitIds, ct))?.ToDictionary(u => u.Id, u => u.Archived)
                ?? throw new InvalidRequestException(IModuleUnits.NotSetUp), "единица измерения"));
        if (Ids(materialRefs) is { Count: > 0 } nomenclatureIds)
            problems.AddRange(Judge(materialRefs,
                (await catalog.RefsAsync(FunctionalTag.RoleNomenclature, nomenclatureIds, ct))?.ToDictionary(r => r.Id, r => r.Archived)
                ?? throw new InvalidRequestException(NoNomenclature),
                "позиция номенклатуры материала"));
        problems.AddRange(await PlanItemProblemsAsync(estimate, numbered, planItems, ct));

        if (problems.Count > 0)
            throw new InvalidRequestException(string.Join("\n", problems.Distinct()));
    }

    /// <summary>Справочник номенклатуры не назначен — материалам сметы ссылаться не на что.</summary>
    internal const string NoNomenclature =
        "Справочник номенклатуры не назначен, поэтому материалы строк сметы сослаться на позицию не могут. " +
        "Ни один тип не несёт тэг типа «Справочник номенклатуры» (либо несут несколько). Назначает " +
        "администратор: «Настройка системы» → «Сопоставление полей».";

    private static async Task<IEnumerable<string>> PlanItemProblemsAsync(
        Estimate estimate, List<(int Number, EstimateRowDraft Row)> rows, IModulePlanItems planItems, CancellationToken ct)
    {
        var refs = rows.Select(x => (x.Number, x.Row.PlanItemId)).ToList();
        if (Ids(refs) is not { Count: > 0 } ids) return [];

        var found = (await planItems.GetAsync(ids, ct)).ToDictionary(p => p.Id, p => p.Key);
        var problems = new List<string>();
        foreach (var (number, row) in rows)
        {
            if (row.PlanItemId is not { } id) continue;
            var where = $"Строка {number}, позиция перечня работ";
            if (!found.TryGetValue(id, out var key))
                problems.Add($"{where}: записи нет — её удалили либо это запись другого вида.");
            else if (key.ConstructionId != estimate.ConstructionId)
                problems.Add($"{where}: позиция другой стройки — объём строки ушёл бы в чужой план.");
            else if (estimate.SectionId is { } section && key.SectionId != section)
                problems.Add($"{where}: позиция другого раздела, а смета составлена на раздел.");
            else if (row.WorkTypeId is { } workType && key.WorkTypeId != workType)
                problems.Add($"{where}: у позиции другой вид работы, чем у строки.");
            else if (row.UnitId is { } unit && key.UnitId != unit)
                problems.Add($"{where}: позиция ведётся в другой единице, чем объём строки.");
        }
        return problems;
    }

    private static List<Guid> Ids(IEnumerable<(int Number, Guid? Id)> refs) => [.. refs.Select(r => r.Id).OfType<Guid>().Distinct()];

    /// <param name="found">Записи, найденные справочником: идентификатор → в архиве ли.</param>
    private static IEnumerable<string> Judge(
        IEnumerable<(int Number, Guid? Id)> refs, IReadOnlyDictionary<Guid, bool> found, string what)
    {
        foreach (var (number, id) in refs)
        {
            if (id is not { } value) continue;
            if (!found.TryGetValue(value, out var archived))
                yield return $"Строка {number}, {what}: записи нет — её удалили либо это запись другого вида.";
            else if (archived)
                yield return $"Строка {number}, {what}: запись в архиве — новая ссылка на неё не записывается. Выберите " +
                             "действующую запись либо верните эту из архива.";
        }
    }
}
