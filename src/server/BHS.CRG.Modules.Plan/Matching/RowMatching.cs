using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Modules.Plan.Matching;

/// <summary>Выбор сметчика: вид работы, единица классификатора (не названа — единица вида работы) и
/// коэффициент (не назван — предложенный единицами).</summary>
public sealed record RateChoice(Guid WorkTypeId, Guid? UnitId, decimal? Factor);

/// <summary>Чем сопоставили: обе единицы и коэффициент между ними — ответ называет их, чтобы ошибка «в сто
/// раз» была видна сразу (Risks design <c>plan-08</c>).</summary>
public sealed record MatchUnits(string? SourceUnit, string? Unit, decimal? Factor);

/// <summary>Строка раздела, которую массовое сопоставление не тронуло, — и почему.</summary>
/// <param name="Reason"><c>alreadyMatched</c> — уже сопоставлена; <c>differentUnit</c> — единица расценки не та,
/// к которой назван коэффициент; <c>material</c> — строка-материал, вида работы ей не нужно (D2 <c>plan-31</c>).</param>
public sealed record SkippedRow(Guid RowId, int Order, string Reason, string Text)
{
    public const string AlreadyMatched = "alreadyMatched";
    public const string DifferentUnit = "differentUnit";
    public const string Material = "material";
}

public sealed record RowMatchOutcome(MatchUnits Units, MatchMemory Memory);

public sealed record SectionMatchOutcome(int Matched, IReadOnlyList<SkippedRow> Skipped, MatchUnits Units, MatchMemory Memory);

/// <summary>
/// Сопоставление строк редакции с видом работы (change <c>plan-08-matching</c>, ТЗ PLAN-6.1, PLAN-4, PLAN-5,
/// D7, D8 зонтика): по строке и массово — разделом ЛСР.
///
/// <list type="number">
///   <item>вид работы и единица — новые ссылки: на месте и не в архиве (ТЗ CORE-34.4);</item>
///   <item>позиция перечня — портом «найди или создай» (<c>plan-04</c>) ДО записи строк: повтор безопасен,
///   а позиция без держателя — допустимое состояние;</item>
///   <item>строки и отметка редакции — одной фиксацией: версия редакции сдвигается, и вторая вкладка получит
///   409, а не молча поверх;</item>
///   <item>запоминание — после, вне транзакции строк (<see cref="RateMatching.RememberSafelyAsync" />).</item>
/// </list>
///
/// <para><b>Зафиксированная редакция</b> (решение В-6 (а)): несопоставленную строку сопоставить можно — это не
/// меняет ни объёмов, ни цен производной; пересопоставить сопоставленную или снять сопоставление — 409 с
/// названием производной и путём «новая редакция».</para>
/// </summary>
public sealed class RowMatching(
    PlanDbContext db, IModuleWorkTypes workTypes, IModuleUnits units, IModulePlanItems planItems, IModuleUser user,
    IModuleActivityLog log, ILogger<RowMatching> logger)
{
    /// <summary>Отказ виду работы для строки-материала (D2 <c>plan-31</c>) — и путь, которым её сопоставляют.</summary>
    public const string NotWork =
        "Это материал, а не работа: вида работы строке не нужно, в план она не входит. Сопоставьте её материал с " +
        "номенклатурой.";

    /// <summary>Сопоставить строку; <paramref name="choice" /> = <c>null</c> — снять сопоставление.</summary>
    public async Task<RowMatchOutcome> MatchRowAsync(
        EstimateRevision revision, EstimateRow row, RateChoice? choice, bool remember, CancellationToken ct)
    {
        if (choice is null)
        {
            if (row.Bare) return new(Units(row, null), MatchMemory.Nothing);
            if (!row.Unmatched) revision.EnsureEditable("Снять сопоставление строки");
            row.Unmatch();
            revision.RowsMatched(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            var unmatchedIn = await db.Estimates.FirstAsync(e => e.Id == revision.EstimateId, ct);
            await log.RecordAsync(EstimateActions.Unmatched, unmatchedIn.Id.ToString(), EstimateActions.Label(unmatchedIn),
                before: $"редакция {revision.Number}, строка {row.Order}", ct: ct);
            return new(Units(row, null), MatchMemory.Nothing);
        }

        if (row.IsMaterial) throw new InvalidRequestException(NotWork);
        var (workType, unit) = await ResolveAsync(choice, ct);
        var estimate = await db.Estimates.FirstAsync(e => e.Id == revision.EstimateId, ct);
        var factor = RateMatching.FactorOf(choice.Factor, row.SourceUnit, unit);
        // Тот же выбор строку не меняет, но запомнить его можно: сопоставивший без запоминания передумал.
        if (!row.MatchedAs(workType.Id, unit.Id, factor))
        {
            if (!row.Unmatched) revision.EnsureEditable("Пересопоставить строку");
            row.Match(workType.Id, unit.Id, factor, await PlanItemAsync(estimate, workType, unit, ct), rateMatchId: null);
            revision.RowsMatched(DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(ct);
            await log.RecordAsync(EstimateActions.Matched, estimate.Id.ToString(), EstimateActions.Label(estimate),
                after: $"редакция {revision.Number}, строка {row.Order}: {workType.DisplayName}, {unit.Name} × {factor}", ct: ct);
        }

        var memory = remember && RateMatchKey.Of(row.RateCode, revision.NormativeBase) is { } key
            ? await RateMatching.RememberSafelyAsync(db,
                [new(key, new(workType.Id, unit.Id, factor, row.SourceUnit))], user, logger, ct)
            : MatchMemory.Nothing;
        return new(Units(row, unit), memory);
    }

    /// <summary>
    /// Сопоставить раздел ЛСР одним действием: несопоставленные строки раздела в единице расценки
    /// <paramref name="sourceUnit" /> (не названа — единица первой несопоставленной) — одним видом работы и
    /// коэффициентом. Сопоставленные и строки другой единицы пропускаются с причиной: коэффициент, названный
    /// для «100 м», к строке в «м» ошибся бы во сто раз.
    /// </summary>
    public async Task<SectionMatchOutcome> MatchSectionAsync(
        EstimateRevision revision, string sectionCode, RateChoice choice, string? sourceUnit, bool remember,
        CancellationToken ct)
    {
        var rows = await db.EstimateRows
            .Where(r => r.RevisionId == revision.Id && r.LsrSectionCode == sectionCode)
            .OrderBy(r => r.Order)
            .ToListAsync(ct);
        if (rows.Count == 0)
            throw new NotFoundException($"В редакции нет раздела ЛСР «{sectionCode}» — перечитайте строки редакции.");
        return await MatchRowsAsync(revision, rows, choice, sourceUnit, remember,
            $"раздел «{rows[0].LsrSectionName ?? sectionCode}»", ct);
    }

    /// <summary>
    /// Сопоставить выбранные строки или несколько разделов ЛСР одним действием (D9 <c>plan-31</c>): то же правило,
    /// что у раздела, — и одна фиксация на все строки: версия редакции сдвигается один раз. Строк больше
    /// <see cref="EstimateRowsEndpoints.MaxLimit" /> за раз — отказ с числом: столько не покажет и страница строк.
    /// </summary>
    /// <param name="lineIds">Строки редакции; <c>null</c> — названы разделы.</param>
    /// <param name="sections">Коды разделов ЛСР; <c>null</c> — названы строки.</param>
    public async Task<SectionMatchOutcome> MatchLinesAsync(
        EstimateRevision revision, IReadOnlyCollection<Guid>? lineIds, IReadOnlyCollection<string>? sections,
        RateChoice choice, string? sourceUnit, bool remember, CancellationToken ct)
    {
        if ((lineIds is { Count: > 0 }) == (sections is { Count: > 0 }))
            throw new InvalidRequestException(
                "Назовите либо строки («lineIds»), либо разделы ЛСР («sections») — одно из двух и не пустым.");
        if (lineIds is { Count: > EstimateRowsEndpoints.MaxLimit })
            throw new InvalidRequestException(TooMany(lineIds.Count));

        var ids = lineIds?.Distinct().ToList();
        var codes = sections?.Distinct().ToList();
        var query = db.EstimateRows.Where(r => r.RevisionId == revision.Id);
        query = ids is not null
            ? query.Where(r => ids.Contains(r.Id))
            : query.Where(r => r.LsrSectionCode != null && codes!.Contains(r.LsrSectionCode));
        var count = await query.CountAsync(ct);
        if (count > EstimateRowsEndpoints.MaxLimit) throw new InvalidRequestException(TooMany(count));
        var rows = await query.OrderBy(r => r.Order).ToListAsync(ct);

        if (ids is not null && rows.Count < ids.Count)
            throw new NotFoundException(
                $"Строк нет в этой редакции: {ids.Count - rows.Count} из {ids.Count} — перечитайте строки редакции.");
        if (codes is not null && codes.Except(rows.Select(r => r.LsrSectionCode!)).ToList() is { Count: > 0 } missing)
            throw new NotFoundException(
                $"В редакции нет разделов ЛСР: {string.Join(", ", missing.Select(m => $"«{m}»"))} — перечитайте строки редакции.");

        var what = ids is not null
            ? "выбранные строки"
            : $"разделы {string.Join(", ", rows.Select(r => r.LsrSectionName ?? r.LsrSectionCode).Distinct().Select(n => $"«{n}»"))}";
        return await MatchRowsAsync(revision, rows, choice, sourceUnit, remember, what, ct);
    }

    private static string TooMany(int count) =>
        $"За раз сопоставляется не больше {EstimateRowsEndpoints.MaxLimit} строк, а названо {count} — разбейте выбор.";

    /// <summary>Строки — одним видом работы и коэффициентом: несопоставленные в единице расценки <paramref name="sourceUnit" />.</summary>
    /// <param name="what">Что сопоставлено, для журнала: «раздел «…»», «выбранные строки».</param>
    private async Task<SectionMatchOutcome> MatchRowsAsync(
        EstimateRevision revision, List<EstimateRow> rows, RateChoice choice, string? sourceUnit, bool remember,
        string what, CancellationToken ct)
    {
        var estimate = await db.Estimates.FirstAsync(e => e.Id == revision.EstimateId, ct);
        var (workType, unit) = await ResolveAsync(choice, ct);
        var source = sourceUnit ?? rows.FirstOrDefault(r => r.Unmatched)?.SourceUnit;
        var open = rows.Where(r => r.Unmatched && RateMatching.SameUnit(r.SourceUnit, source)).ToList();
        decimal? factor = open.Count > 0 ? RateMatching.FactorOf(choice.Factor, source, unit) : choice.Factor;

        var skipped = rows.Except(open).Select(r => r.IsMaterial
                ? new SkippedRow(r.Id, r.Order, SkippedRow.Material, NotWork)
                : r.Unmatched
                ? new SkippedRow(r.Id, r.Order, SkippedRow.DifferentUnit,
                    $"Единица расценки «{r.SourceUnit}», а коэффициент назван для «{source}» — сопоставьте строку отдельно.")
                : new SkippedRow(r.Id, r.Order, SkippedRow.AlreadyMatched, "Строка уже сопоставлена."))
            .ToList();
        var units = new MatchUnits(source, unit.Name, factor);
        if (open.Count == 0) return new(0, skipped, units, MatchMemory.Nothing);

        var planItem = await PlanItemAsync(estimate, workType, unit, ct);
        foreach (var row in open) row.Match(workType.Id, unit.Id, factor!.Value, planItem, rateMatchId: null);
        revision.RowsMatched(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        await log.RecordAsync(EstimateActions.Matched, estimate.Id.ToString(), EstimateActions.Label(estimate),
            after: $"редакция {revision.Number}, {what}: строк {open.Count} — {workType.DisplayName}, {unit.Name} × {factor}", ct: ct);

        var memory = remember
            ? await RateMatching.RememberSafelyAsync(db, RateMatching.Worth(open
                .Select(r => (Key: RateMatchKey.Of(r.RateCode, revision.NormativeBase), Row: r))
                .Where(x => x.Key is not null)
                .Select(x => new RateToRemember(x.Key!, new(workType.Id, unit.Id, factor!.Value, x.Row.SourceUnit)))), user, logger, ct)
            : MatchMemory.Nothing;
        return new(open.Count, skipped, units, memory);
    }

    /// <summary>
    /// Вид работы и единица выбора — на месте и не в архиве. Единица не названа — единица вида работы (ТЗ
    /// PLAN-6.1): «прокладка кабеля, м» и «…, шт» — разные позиции перечня (ТЗ CORE-10), и выбрать другую
    /// можно, назвав её.
    /// </summary>
    internal async Task<(ModuleWorkType WorkType, ModuleUnit Unit)> ResolveAsync(RateChoice choice, CancellationToken ct)
    {
        var workType = (await workTypes.GetAsync([choice.WorkTypeId], ct)
                ?? throw new InvalidRequestException(IModuleWorkTypes.NotSetUp))
            .FirstOrDefault(w => w.Id == choice.WorkTypeId)
            ?? throw new InvalidRequestException("Такого вида работы нет — его удалили либо это запись другого вида.");
        if (workType.Archived)
            throw new InvalidRequestException(
                $"Вид работы «{workType.DisplayName}» в архиве — новая строка на него не сопоставляется. Выберите действующий.");

        var unitId = choice.UnitId ?? workType.UnitId
            ?? throw new InvalidRequestException(
                $"У вида работы «{workType.DisplayName}» не стоит единица — назовите единицу классификатора («unitId»).");
        var unit = (await units.GetAsync([unitId], ct) ?? throw new InvalidRequestException(IModuleUnits.NotSetUp))
            .FirstOrDefault(u => u.Id == unitId)
            ?? throw new InvalidRequestException("Такой единицы измерения нет — её удалили либо это запись другого вида.");
        if (unit.Archived)
            throw new InvalidRequestException($"Единица «{unit.Name}» в архиве — выберите действующую.");
        return (workType, unit);
    }

    /// <summary>Позиция перечня «вид работы, стройка, раздел сметы, единица» — «стройка в целом» у сметы без раздела.</summary>
    private async Task<Guid> PlanItemAsync(Estimate estimate, ModuleWorkType workType, ModuleUnit unit, CancellationToken ct)
    {
        var outcome = await planItems.FindOrCreateAsync(
            new PlanItemKey(workType.Id, estimate.ConstructionId, estimate.SectionId, unit.Id), ct);
        return outcome.Found
            ? outcome.Item.Id
            : throw new InvalidRequestException($"Позиция перечня работ не заведена: {outcome.Refusal.Reason}.");
    }

    private static MatchUnits Units(EstimateRow row, ModuleUnit? unit) => new(row.SourceUnit, unit?.Name, row.Factor);
}
