using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>Строка будущей редакции — как её разобрал импорт (<c>plan-07</c>).</summary>
/// <param name="Data">Сметное наполнение по схеме <c>ПозицияСметы</c> — JSON-объект; <c>null</c> — пусто.</param>
/// <param name="Labor">Трудозатраты рабочих, чел.-ч на объём строки, — в порядке файла.</param>
/// <param name="RateMatchId">Запомненное сопоставление, подставившее вид работы (<c>plan-08</c>).</param>
/// <param name="Number">Номер позиции в файле как есть, сквозной (<c>plan-31</c>).</param>
/// <param name="Subordinate">Подчинённая позиция файла.</param>
/// <param name="Kind">Работа или материал (D2 <c>plan-31</c>).</param>
public sealed record EstimateRowDraft(
    string Name,
    string? LsrSectionCode = null, string? LsrSectionName = null, string? NumberInSection = null,
    string? RateCode = null, decimal? SourceVolume = null, string? SourceUnit = null,
    Guid? WorkTypeId = null, Guid? UnitId = null, decimal? Factor = null, decimal? Volume = null,
    Guid? PlanItemId = null, decimal? UnitCost = null, decimal? Total = null, string? Data = null,
    IReadOnlyList<EstimateMaterialDraft>? Materials = null, IReadOnlyList<decimal>? Labor = null,
    Guid? RateMatchId = null, string? Number = null, bool Subordinate = false,
    EstimateRowKind Kind = EstimateRowKind.Work);

/// <summary>Материал строки будущей редакции.</summary>
/// <param name="ResourceMatchId">Запомненное сопоставление, подставившее позицию номенклатуры (<c>plan-08</c>).</param>
public sealed record EstimateMaterialDraft(
    string SourceName, decimal Quantity, string? SourceCode = null, string? SourceUnit = null,
    Guid? NomenclatureId = null, decimal? UnitPrice = null, decimal? Amount = null, Guid? ResourceMatchId = null);

/// <summary>
/// Запись новой редакции сметы — ЕДИНСТВЕННОЕ место, где строки сметы попадают в базу (change
/// <c>plan-06-estimate-model</c>). Зовёт его импорт (<c>plan-07</c>); здесь — то, что импорт сделать
/// обязан и забыть не должен:
/// <list type="bullet">
///   <item>сметное наполнение каждой строки проходит охрану записи ядра (<c>IModuleWriteGuard</c>,
///   ТЗ CORE-20) — тип <c>ПозицияСметы</c> открыт, и поля заказчика проверяются по его схеме;</item>
///   <item>ссылки строк проверены (<see cref="EstimateRowReferences" />): на месте, не в архиве (ТЗ
///   CORE-34.4), нужного вида, а позиция перечня — этой стройки и того же вида работы и единицы;</item>
///   <item>номер редакции растёт в смете, а первая редакция становится действующей сама (решение В-5,
///   ТЗ PLAN-5.2).</item>
/// </list>
///
/// <para>Редакция пишется одной фиксацией: строки без шапки или шапка без половины строк были бы
/// редакцией, которой в файле не было.</para>
/// </summary>
public sealed class EstimateRevisionWriter(
    PlanDbContext db, IModuleTypes types, IModuleWriteGuard writeGuard, IModuleWorkTypes workTypes,
    IModuleUnits units, IModulePlanItems planItems, IModuleCatalog catalog, IModuleUser user)
{
    /// <summary>Записать редакцию со строками; ответ — заведённая редакция.</summary>
    public async Task<EstimateRevision> AddAsync(
        Guid estimateId, EstimateRevisionSource source, IReadOnlyList<EstimateRowDraft> rows, CancellationToken ct = default)
    {
        var estimate = await db.Estimates.FirstOrDefaultAsync(e => e.Id == estimateId, ct)
            ?? throw new NotFoundException("Смета не найдена — возможно, её удалили.");
        var revision = await StageAsync(estimate, source, rows, ct);
        await db.SaveChangesAsync(ct);
        return revision;
    }

    /// <summary>
    /// Положить редакцию со строками в контекст, НЕ сохраняя: всё проверено, и сохранение — дело звавшего.
    /// Так импорт (<c>plan-07</c>) пишет редакцию одной фиксацией вместе со своим исходом — редакция без
    /// отметки у импорта или отметка без редакции были бы состоянием, которого не было.
    /// </summary>
    /// <param name="estimate">Смета, отслеживаемая этим контекстом.</param>
    public async Task<EstimateRevision> StageAsync(
        Estimate estimate, EstimateRevisionSource source, IReadOnlyList<EstimateRowDraft> rows, CancellationToken ct = default)
    {
        foreach (var (row, i) in rows.Select((r, i) => (r, i)))
            if (string.IsNullOrWhiteSpace(row.Name))
                throw new InvalidRequestException($"Строка {i + 1}: нет наименования.");

        await EstimateRowReferences.EnsureAsync(estimate, rows, workTypes, units, planItems, catalog, ct);
        var typeId = await types.FindAsync(PlanRecordTypes.EstimateRowCode, ct)
            ?? throw new ConflictException(
                $"Тип «{PlanRecordTypes.EstimateRow.Name}» на экземпляре не заведён — сметное наполнение строк " +
                "не с чем сверить. Он заводится при старте приложения с модулем «Планирование»; перезапустите " +
                "приложение и повторите.");

        var number = (await db.EstimateRevisions.Where(r => r.EstimateId == estimate.Id)
            .MaxAsync(r => (int?)r.Number, ct) ?? 0) + 1;
        var revision = EstimateRevision.Create(estimate.Id, number, source, user.Id, user.Name, DateTimeOffset.UtcNow);

        // Сметное наполнение проходит охрану записи одним пакетом: тип и справочники схемы читаются раз
        // на редакцию, а не на строку, и одинаковое наполнение спрашивается один раз. Отказ называет все
        // строки разом, поэтому строки копятся, а в контекст попадают, только если отказов нет.
        var data = rows.Select((r, i) => Normalized(r.Data, i + 1)).ToList();
        var distinct = data.Distinct(StringComparer.Ordinal).ToList();
        var verdicts = distinct.Zip(await writeGuard.CreationRefusalsAsync(typeId, distinct, ct))
            .ToDictionary(v => v.First, v => v.Second, StringComparer.Ordinal);
        var refusals = new List<string>();
        var created = new List<(EstimateRow Row, EstimateRowDraft Draft)>();
        foreach (var (draft, i) in rows.Select((r, i) => (r, i)))
        {
            refusals.AddRange(verdicts[data[i]].Select(r => $"Строка {i + 1}{(r.Path is null ? "" : $", поле {r.Path}")}: {r.Message}"));
            created.Add((EstimateRow.CreateEstimateRow(revision.Id, i + 1, draft, data[i]), draft));
        }

        if (refusals.Count > 0)
            throw new InvalidRequestException("Сметное наполнение строк не прошло проверку:\n" + string.Join("\n", refusals));

        db.EstimateRevisions.Add(revision);
        foreach (var (row, draft) in created)
        {
            db.EstimateRows.Add(row);
            foreach (var (material, m) in (draft.Materials ?? []).Select((x, m) => (x, m)))
                db.EstimateRowMaterials.Add(EstimateRowMaterial.Create(row.Id, m + 1, material));
            foreach (var (hours, l) in (draft.Labor ?? []).Select((x, l) => (x, l)))
                db.EstimateRowLabor.Add(EstimateRowLabor.Create(row.Id, l + 1, hours));
        }

        if (estimate.CurrentRevisionId is null) estimate.MakeCurrent(revision);
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
}
