using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Endpoints;
using BHS.CRG.Modules.Plan.GrandSmeta;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Import;

/// <summary>Строка редакции, оставшаяся несопоставленной, — и почему: позиция перечня не заведена.</summary>
/// <param name="Row">Номер строки в редакции, с единицы.</param>
public sealed record UnmatchedImportRow(int Row, string Reason);

/// <summary>Чем кончилось создание редакции из импорта.</summary>
/// <param name="EstimateCreated">Импорт завёл новую смету.</param>
/// <param name="Repeated">Редакция создана раньше — этим же импортом из другой вкладки или прежним нажатием.</param>
public sealed record EstimateImportCommitted(
    EstimateImport Import, Estimate Estimate, EstimateRevision Revision, bool EstimateCreated, bool Repeated,
    IReadOnlyList<UnmatchedImportRow> Unmatched);

/// <summary>
/// В файле есть не разобранные позиции, а согласия на частичную редакцию нет (ТЗ PLAN-7). Несёт числа: экран
/// спрашивает человека «создать редакцию из N?», и разбирать для этого фразу он не должен.
/// </summary>
public sealed class PartialImportException(int parsed, int inFile, int unparsed) : ConflictException(
    $"В файле {unparsed} не разобранных позиций: разобрано {parsed} из {inFile}. Редакция из такого файла " +
    $"создаётся только с вашего согласия — подтвердите частичный импорт, и она будет из {parsed} строк.")
{
    public int Parsed { get; } = parsed;
    public int InFile { get; } = inFile;
    public int Unparsed { get; } = unparsed;
}

/// <summary>
/// Создание редакции из разобранного импорта (change <c>plan-07-estimate-import</c>, ТЗ PLAN-6, PLAN-7,
/// STG-6.1, D6 зонтика).
///
/// <list type="number">
///   <item>подстановка сопоставлений — <see cref="IEstimateRowMatcher" />;</item>
///   <item>позиции перечня — портом ядра «найди или создай» (<c>IModulePlanItems</c>), ДО строк и своей
///   транзакцией ядра (ТЗ CORE-4): общей у двух схем нет, и повтор после сбоя найдёт те же позиции;</item>
///   <item>смета (если новая), редакция со строками и исход импорта — одной транзакцией модуля.</item>
/// </list>
///
/// <para><b>Повтор безопасен.</b> Ключ импорта у редакции единствен: проигравшее одновременное создание
/// получает отказ базы, перечитывает импорт и отвечает уже созданной редакцией, а не второй.</para>
///
/// <para>Версию импорта сверяет адрес (<c>If-Match</c>) до вызова: две вкладки, одна из которых видела
/// импорт до создания редакции, получат 409, а не молча ту же редакцию.</para>
/// </summary>
public sealed class EstimateImportCommit(
    PlanDbContext db, IEstimateRowMatcher matcher, IModulePlanItems planItems, EstimateRevisionWriter writer,
    IModuleActivityLog log)
{
    /// <summary>Создать редакцию.</summary>
    /// <param name="import">Импорт, отслеживаемый контекстом модуля, — версия его уже сверена.</param>
    /// <param name="acceptPartial">Человек согласился на редакцию без не разобранных позиций.</param>
    public async Task<EstimateImportCommitted> RunAsync(EstimateImport import, bool acceptPartial, CancellationToken ct)
    {
        if (import.State == EstimateImportState.Committed) return await ExistingAsync(import.Id, ct);
        var model = Committable(import);
        var unparsed = model.Unparsed.Count;
        var parsed = model.Positions.Count();
        // Ни одной позиции — редакции не из чего быть: пустая первая редакция стала бы действующей, и план
        // стройки по ней молча вышел бы пустым. Согласие на частичный файл этого не меняет.
        if (parsed == 0)
            throw new ConflictException(unparsed > 0
                ? $"В файле не разобрана ни одна позиция из {unparsed} — редакции из него не создать. Отчёт разбора " +
                  "называет причины; исправьте файл и загрузите его заново."
                : "В файле нет ни одной позиции — редакции из него не создать.");
        if (unparsed > 0 && !acceptPartial) throw new PartialImportException(parsed, model.PositionsInFile, unparsed);
        // Неактивные позиции строками не становятся (D3 plan-31) — и согласия не требуют; но если активных нет
        // вовсе, редакция вышла бы пустой по той же причине, что и выше.
        var positions = EstimateImportRows.Drafts(model);
        if (positions.Count == 0)
            throw new ConflictException("Все позиции файла неактивны — редакции из него не создать.");

        var created = import.EstimateId is null;
        var estimate = import.EstimateId is { } id
            ? await db.Estimates.FirstOrDefaultAsync(e => e.Id == id, ct)
                ?? throw new NotFoundException("Смета, в которую шёл импорт, не найдена — возможно, её удалили.")
            // Стройку и раздел новой сметы держит импорт (PlanReferences) — пока он жив, их не удалить.
            : Estimate.Create(import.ConstructionId, import.NewEstimateSectionId, import.NewEstimateName!,
                import.NewEstimateKind ?? EstimateKind.Main, DateTimeOffset.UtcNow);

        var drafts = await matcher.ApplyCheckedAsync(
            new(estimate.ConstructionId, estimate.SectionId, model.Requisites.NormativeBase), positions, ct);
        var (rows, unmatched) = await WithPlanItemsAsync(estimate, drafts, ct);

        EstimateRevision revision;
        try
        {
            // Одна транзакция на смету, редакцию и исход импорта. Новая смета сохраняется первой: она и её
            // первая редакция ссылаются друг на друга (действующая редакция), и одним сохранением такую пару
            // EF не запишет.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (created)
            {
                db.Estimates.Add(estimate);
                await db.SaveChangesAsync(ct);
            }
            revision = await writer.StageAsync(estimate,
                new EstimateRevisionSource(import.BlobPath, import.FileName, import.SchemaVersion, import.Id.ToString(),
                    model.Requisites.NormativeBase), rows, ct);
            import.Committed(estimate, revision, EstimateImportJson.Write(EstimateImportRows.Skipped(model)));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (ConflictException)
        {
            if (await CommittedMeanwhileAsync(import.Id, ct)) return await ExistingAsync(import.Id, ct);
            throw;
        }

        if (created)
            await log.RecordAsync(EstimateActions.Created, estimate.Id.ToString(), EstimateActions.Label(estimate),
                after: $"импортом файла «{import.FileName}»", ct: ct);
        await log.RecordAsync(EstimateActions.Imported, estimate.Id.ToString(), EstimateActions.Label(estimate),
            after: $"редакция {revision.Number}, строк {rows.Count}, не разобрано {unparsed}", ct: ct);
        return new(import, estimate, revision, created, Repeated: false, unmatched);
    }

    /// <summary>Модель разобранной ЛСР — или отказ словами того, почему из импорта редакции не сделать.</summary>
    private static ParsedLocalEstimate Committable(EstimateImport import)
    {
        switch (import.State)
        {
            case EstimateImportState.Parsing:
                throw new ConflictException("Файл ещё разбирается — редакция создаётся по отчёту разбора. Дождитесь его.");
            case EstimateImportState.Rejected:
                throw new ConflictException($"Файл не разобран, и редакции из него не создать: {import.RefusalText}");
        }
        if (import.Kind is { } kind and not GrandSmetaKind.LocalEstimate)
            throw new ConflictException(EstimateImportViews.NotEstimate(kind));
        return EstimateImportJson.Model(import)
            ?? throw new ConflictException("У импорта нет модели разбора — загрузите файл ещё раз.");
    }

    /// <summary>
    /// Шаг 2: позиции перечня для строк, которым подстановка дала вид работы и единицу. Отказ ключа (вид работы
    /// в архиве…) не роняет создание редакции: строка ложится несопоставленной, а причина уходит в ответ.
    /// Отказавшая составляющая со строки снимается — новая ссылка на архивную запись не записывается
    /// (ТЗ CORE-34.4).
    /// </summary>
    private async Task<(List<EstimateRowDraft> Rows, List<UnmatchedImportRow> Unmatched)> WithPlanItemsAsync(
        Estimate estimate, IReadOnlyList<EstimateRowDraft> drafts, CancellationToken ct)
    {
        var rows = drafts.ToList();
        var unmatched = new List<UnmatchedImportRow>();
        var keyed = rows.Select((r, i) => (Index: i, Key: Key(estimate, r))).Where(x => x.Key is not null)
            .Select(x => (x.Index, Key: x.Key!.Value)).ToList();
        if (keyed.Count == 0) return (rows, unmatched);

        var batch = await planItems.FindOrCreateManyAsync([.. keyed.Select(x => x.Key).Distinct()], ct);
        var refusals = batch.Refusals.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var (index, key) in keyed)
        {
            if (batch.Items.TryGetValue(key, out var item))
            {
                rows[index] = rows[index] with { PlanItemId = item.Id };
                continue;
            }
            var refusal = refusals.GetValueOrDefault(key);
            // Пометка «запомнено» уходит вместе с отказавшей составляющей: подставленным строку больше не назвать.
            rows[index] = refusal?.Part switch
            {
                PlanItemKeyPart.WorkType => rows[index] with { WorkTypeId = null, RateMatchId = null },
                PlanItemKeyPart.Unit => rows[index] with { UnitId = null, Factor = null, Volume = null, RateMatchId = null },
                _ => rows[index] with { RateMatchId = null },
            };
            unmatched.Add(new(index + 1, refusal?.Reason ?? "позиция перечня работ не заведена"));
        }
        return (rows, unmatched);
    }

    private static PlanItemKey? Key(Estimate estimate, EstimateRowDraft row) =>
        row is { PlanItemId: null, WorkTypeId: { } workType, UnitId: { } unit }
            ? new PlanItemKey(workType, estimate.ConstructionId, estimate.SectionId, unit)
            : null;

    /// <summary>Создание проиграло — не потому ли, что редакцию из этого импорта успели создать раньше.</summary>
    private async Task<bool> CommittedMeanwhileAsync(Guid importId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        return await db.EstimateImports.AnyAsync(
            i => i.Id == importId && i.State == EstimateImportState.Committed && i.RevisionId != null, ct);
    }

    private async Task<EstimateImportCommitted> ExistingAsync(Guid importId, CancellationToken ct)
    {
        var import = await db.EstimateImports.FirstAsync(i => i.Id == importId, ct);
        var revision = await db.EstimateRevisions.FirstAsync(r => r.Id == import.RevisionId, ct);
        var estimate = await db.Estimates.FirstAsync(e => e.Id == revision.EstimateId, ct);
        return new(import, estimate, revision, EstimateCreated: false, Repeated: true, []);
    }
}
