using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>Строка будущей редакции — как её разобрал импорт (<c>plan-07</c>).</summary>
/// <param name="Data">Сметное наполнение по схеме <c>ПозицияСметы</c> — JSON-объект; <c>null</c> — пусто.</param>
/// <param name="Labor">Трудозатраты рабочих, чел.-ч на объём строки.</param>
public sealed record EstimateRowDraft(
    string Name,
    string? LsrSectionCode = null, string? LsrSectionName = null, string? NumberInSection = null,
    string? RateCode = null, decimal? SourceVolume = null, string? SourceUnit = null,
    Guid? WorkTypeId = null, Guid? UnitId = null, decimal? Factor = null, decimal? Volume = null,
    Guid? PlanItemId = null, decimal? UnitCost = null, decimal? Total = null, string? Data = null,
    IReadOnlyList<EstimateMaterialDraft>? Materials = null, IReadOnlyList<decimal>? Labor = null);

/// <summary>Материал строки будущей редакции.</summary>
public sealed record EstimateMaterialDraft(
    string SourceName, decimal Quantity, string? SourceCode = null, string? SourceUnit = null,
    Guid? NomenclatureId = null, decimal? UnitPrice = null, decimal? Amount = null);

/// <summary>
/// Запись новой редакции сметы — ЕДИНСТВЕННОЕ место, где строки сметы попадают в базу (change
/// <c>plan-06-estimate-model</c>). Зовёт его импорт (<c>plan-07</c>); здесь — то, что импорт сделать
/// обязан и забыть не должен:
/// <list type="bullet">
///   <item>сметное наполнение каждой строки проходит охрану записи ядра (<c>IModuleWriteGuard</c>,
///   ТЗ CORE-20) — тип <c>ПозицияСметы</c> открыт, и поля заказчика проверяются по его схеме;</item>
///   <item>новые ссылки на записи ядра ведут к записям на месте и не в архиве (ТЗ CORE-34.4) — все
///   ссылки редакции новые: она заводится целиком;</item>
///   <item>номер редакции растёт в смете, а первая редакция становится действующей сама (решение В-5,
///   ТЗ PLAN-5.2).</item>
/// </list>
///
/// <para>Редакция пишется одной фиксацией: строки без шапки или шапка без половины строк были бы
/// редакцией, которой в файле не было.</para>
/// </summary>
public sealed class EstimateRevisionWriter(
    PlanDbContext db, IModuleTypes types, IModuleWriteGuard writeGuard, IModuleWorkTypes workTypes,
    IModuleUnits units, IModuleReferenceTargets targets, IModuleUser user)
{
    /// <summary>Записать редакцию со строками; ответ — заведённая редакция.</summary>
    public async Task<EstimateRevision> AddAsync(
        Guid estimateId, EstimateRevisionSource source, IReadOnlyList<EstimateRowDraft> rows, CancellationToken ct = default)
    {
        var estimate = await db.Estimates.FirstOrDefaultAsync(e => e.Id == estimateId, ct)
            ?? throw new NotFoundException("Смета не найдена — возможно, её удалили.");

        foreach (var (row, i) in rows.Select((r, i) => (r, i)))
            if (string.IsNullOrWhiteSpace(row.Name))
                throw new InvalidRequestException($"Строка {i + 1}: нет наименования.");

        await EnsureReferencesAsync(rows, ct);
        var typeId = await types.FindAsync(PlanRecordTypes.EstimateRowCode, ct)
            ?? throw new ConflictException(
                $"Тип «{PlanRecordTypes.EstimateRow.Name}» на экземпляре не заведён — сметное наполнение строк " +
                "не с чем сверить. Он заводится при старте приложения с модулем «Планирование»; перезапустите " +
                "приложение и повторите.");

        var number = (await db.EstimateRevisions.Where(r => r.EstimateId == estimateId)
            .MaxAsync(r => (int?)r.Number, ct) ?? 0) + 1;
        var revision = EstimateRevision.Create(estimateId, number, source, user.Id, user.Name, DateTimeOffset.UtcNow);

        // Сметное наполнение каждой строки проходит охрану записи. Одинаковое спрашивается один раз:
        // строк в редакции тысячи, а наполнение у них часто совпадает. Отказ называет все строки разом,
        // поэтому строки копятся, а в контекст попадают, только если отказов нет.
        var refusals = new List<string>();
        var guarded = new Dictionary<string, IReadOnlyList<ModuleWriteRefusal>>(StringComparer.Ordinal);
        var created = new List<(EstimateRow Row, EstimateRowDraft Draft)>();
        foreach (var (draft, i) in rows.Select((r, i) => (r, i)))
        {
            var data = Normalized(draft.Data, i + 1);
            if (!guarded.TryGetValue(data, out var found))
                guarded[data] = found = await writeGuard.RefusalsAsync(typeId, null, data, ct);
            refusals.AddRange(found.Select(r => $"Строка {i + 1}{(r.Path is null ? "" : $", поле {r.Path}")}: {r.Message}"));
            created.Add((EstimateRow.CreateEstimateRow(revision.Id, i + 1, draft, data), draft));
        }

        if (refusals.Count > 0)
            throw new InvalidRequestException("Сметное наполнение строк не прошло проверку:\n" + string.Join("\n", refusals));

        db.EstimateRevisions.Add(revision);
        foreach (var (row, draft) in created)
        {
            db.EstimateRows.Add(row);
            foreach (var (material, m) in (draft.Materials ?? []).Select((x, m) => (x, m)))
                db.EstimateRowMaterials.Add(EstimateRowMaterial.Create(row.Id, m + 1, material));
            foreach (var hours in draft.Labor ?? [])
                db.EstimateRowLabor.Add(EstimateRowLabor.Create(row.Id, hours));
        }

        if (estimate.CurrentRevisionId is null) estimate.MakeCurrent(revision);

        await db.SaveChangesAsync(ct);
        return revision;
    }

    private static string Normalized(string? json, int number)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidRequestException($"Строка {number}: сметное наполнение — не JSON-объект.");
            return doc.RootElement.GetRawText();
        }
        catch (JsonException e)
        {
            throw new InvalidRequestException($"Строка {number}: сметное наполнение — не JSON.", e);
        }
    }

    /// <summary>
    /// Новые ссылки редакции (ТЗ CORE-34.4): вид работы и единица — записи своих справочников, позиция
    /// перечня и номенклатура — записи ядра; каждая на месте и не в архиве. Отказ называет все строки разом.
    /// </summary>
    private async Task EnsureReferencesAsync(IReadOnlyList<EstimateRowDraft> rows, CancellationToken ct)
    {
        var numbered = rows.Select((r, i) => (Number: i + 1, Row: r)).ToList();
        var workTypeRefs = numbered.Select(x => (x.Number, x.Row.WorkTypeId)).ToList();
        var unitRefs = numbered.Select(x => (x.Number, x.Row.UnitId)).ToList();
        var planItemRefs = numbered.Select(x => (x.Number, x.Row.PlanItemId)).ToList();
        var materialRefs = numbered.SelectMany(x => (x.Row.Materials ?? []).Select(m => (x.Number, m.NomenclatureId))).ToList();

        var problems = new List<string>();
        if (Ids(workTypeRefs) is { Count: > 0 } workTypeIds)
            problems.AddRange(Judge(workTypeRefs, (await workTypes.GetAsync(workTypeIds, ct))?.ToDictionary(w => w.Id, w => w.Archived)
                ?? throw new InvalidRequestException(IModuleWorkTypes.NotSetUp), "вид работы"));
        if (Ids(unitRefs) is { Count: > 0 } unitIds)
            problems.AddRange(Judge(unitRefs, (await units.GetAsync(unitIds, ct))?.ToDictionary(u => u.Id, u => u.Archived)
                ?? throw new InvalidRequestException(IModuleUnits.NotSetUp), "единица измерения"));
        problems.AddRange(Judge(planItemRefs,
            await StatesAsync(ReferenceTarget.WorkPlanItem, Ids(planItemRefs), ct), "позиция перечня работ"));
        problems.AddRange(Judge(materialRefs,
            await StatesAsync(ReferenceTarget.Record, Ids(materialRefs), ct), "позиция номенклатуры материала"));

        if (problems.Count > 0)
            throw new InvalidRequestException(string.Join("\n", problems.Distinct()));
    }

    private async Task<Dictionary<Guid, bool>> StatesAsync(ReferenceTarget target, List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var states = await targets.StatesAsync(target, ids, ct);
        // Потерянная запись в словарь не попадает — её Judge назовёт «нет».
        return states.Where(s => s.Value != ReferenceState.Lost).ToDictionary(s => s.Key, s => s.Value == ReferenceState.Archived);
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
