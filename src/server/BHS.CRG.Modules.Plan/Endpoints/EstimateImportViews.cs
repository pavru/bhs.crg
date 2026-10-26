using System.Globalization;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.GrandSmeta;
using BHS.CRG.Modules.Plan.Import;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Отчёт импорта (change <c>plan-07-estimate-import</c>, ТЗ PLAN-7): одним местом на загрузку, чтение и отмену.
/// Итоги разделов и сметы — только с <c>plan.estimate.cost</c> (D4 зонтика): без права их в ответе нет, а
/// корень называет скрытое.
/// </summary>
internal static class EstimateImportViews
{
    public static async Task<EstimateImportDto> OneAsync(
        PlanDbContext db, IModuleUser user, IModuleJobs jobs, Guid id, CancellationToken ct)
    {
        var found = await db.EstimateImports.AsNoTracking().Where(i => i.Id == id)
            .Select(i => new { Import = i, Version = EF.Property<uint>(i, PlanDbContext.RowVersion) })
            .FirstOrDefaultAsync(ct) ?? throw NotFound();
        var import = found.Import;
        var hidden = await EstimateViews.HiddenAsync(user, ct);
        var cost = hidden is null;

        var marks = new List<PlanMark>();
        string? progress = null;
        if (import is { State: EstimateImportState.Parsing, JobId: { } jobId } && await jobs.GetAsync(jobId, ct) is { } job)
        {
            progress = job.Progress;
            // Задача кончилась, а исхода у импорта нет: разбор оборвался раньше, чем записал его. Состояние
            // перечитывается — исход мог лечь между чтением импорта и чтением задачи.
            if (job.Status is ModuleJobStatus.Failed or ModuleJobStatus.Cancelled or ModuleJobStatus.Succeeded
                && await db.EstimateImports.AnyAsync(i => i.Id == id && i.State == EstimateImportState.Parsing, ct))
                marks.Add(new("interrupted",
                    $"Разбор прерван{(job.Error is { } error ? $": {error}" : "")}. Отмените импорт и загрузите файл ещё раз."));
        }

        var model = EstimateImportJson.Model(import);
        var unparsed = model?.Unparsed ?? EstimateImportJson.Unparsed(import);
        var positions = model?.Positions.ToList();
        if (import.State != EstimateImportState.Rejected && import.Kind is { } kind and not GrandSmetaKind.LocalEstimate)
            marks.Add(new("notEstimate", NotEstimate(kind)));
        if (model is not null && positions is not null)
        {
            if (unparsed.Count > 0)
                marks.Add(new("partial",
                    $"Разобрано {positions.Count} из {model.PositionsInFile}, {unparsed.Count} не разобрано — " +
                    "редакция из такого файла создаётся только с вашего согласия."));
            if (positions.Any(p => p.TotalSource == TotalSource.ComputedBySystem))
                marks.Add(new("computedTotals",
                    "Файл сохранён без итогов — итоги позиций посчитаны системой по данным файла."));
            if (await OtherCalculationAsync(db, import, ct) is { } previous)
                marks.Add(new("otherCalculation",
                    $"Номер расчёта в файле — «{import.LsrNumber}», а у прежней редакции сметы — «{previous}». Проверьте, " +
                    "что это тот же расчёт: иначе стоит импортировать его в новую смету."));
        }

        var target = Target(import.EstimateId,
            import.EstimateId is { } estimateId
                ? await db.Estimates.AsNoTracking().Where(e => e.Id == estimateId).Select(e => e.Name).FirstOrDefaultAsync(ct)
                : null,
            import.NewEstimateName, import.NewEstimateSectionId, import.NewEstimateKind);

        return new EstimateImportDto(
            import.Id, import.ConstructionId, target, import.FileName, StateCode(import.State), KindCode(import.Kind),
            import.Kind is { } k ? GrandSmetaSchemas.KindName(k) : null, import.ProgramVersion, import.SchemaVersion,
            progress,
            import is { RefusalCode: { } code, RefusalText: { } text } ? new EstimateImportRefusalDto(code, text) : null,
            positions?.Count ?? import.ParsedPositions, model?.PositionsInFile ?? import.PositionsInFile,
            [.. unparsed.Select(u => new UnparsedPositionDto(u.Place, u.Line, u.Reason))],
            [.. EstimateImportJson.Skipped(import, model).Select(p => new SkippedPositionDto(p.Place, p.Reason, p.Text))],
            model is null || positions is null ? null : Summary(model, positions, cost),
            marks, import.RevisionId, import.CreatedAt, import.CreatedByName,
            found.Version.ToString(CultureInfo.InvariantCulture), hidden);
    }

    /// <summary>
    /// Импорты стройки, новые сверху, — без модели разбора: её список не читает. По нему находят импорт, который
    /// никто не довёл до редакции, — он держит стройку и раздел новой сметы, пока его не отменят.
    /// </summary>
    public static async Task<List<EstimateImportItemDto>> ListAsync(
        PlanDbContext db, Guid constructionId, Guid? estimateId, CancellationToken ct)
    {
        var query = db.EstimateImports.AsNoTracking().Where(i => i.ConstructionId == constructionId);
        if (estimateId is { } only) query = query.Where(i => i.EstimateId == only);
        var rows = await query.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id)
            .Select(i => new
            {
                i.Id, i.EstimateId, i.NewEstimateName, i.NewEstimateSectionId, i.NewEstimateKind, i.FileName, i.State,
                i.Kind, i.RefusalCode, i.RevisionId, i.CreatedAt, i.CreatedByName,
                Version = EF.Property<uint>(i, PlanDbContext.RowVersion),
            })
            .ToListAsync(ct);

        var ids = rows.Where(r => r.EstimateId is not null).Select(r => r.EstimateId!.Value).Distinct().ToList();
        var names = await db.Estimates.AsNoTracking().Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.Name, ct);

        return
        [
            .. rows.Select(r => new EstimateImportItemDto(
                r.Id,
                Target(r.EstimateId, r.EstimateId is { } id ? names.GetValueOrDefault(id) : null,
                    r.NewEstimateName, r.NewEstimateSectionId, r.NewEstimateKind),
                r.FileName, StateCode(r.State), KindCode(r.Kind), r.RefusalCode, r.RevisionId, r.CreatedAt,
                r.CreatedByName, r.Version.ToString(CultureInfo.InvariantCulture))),
        ];
    }

    public static NotFoundException NotFound() => new("Импорт не найден — возможно, его отменили.");

    private static EstimateImportTargetDto Target(
        Guid? estimateId, string? estimateName, string? newName, Guid? sectionId, EstimateKind? kind) =>
        new(estimateId, estimateName,
            newName is null ? null : new NewEstimateDto(newName, sectionId, EstimateViews.KindCode(kind ?? EstimateKind.Main)));

    /// <summary>Сводка будущей редакции: неактивные позиции строками не станут (D3 <c>plan-31</c>) — и не считаются.</summary>
    private static ImportSummaryDto Summary(ParsedLocalEstimate model, IReadOnlyList<LocalEstimatePosition> positions, bool cost) =>
        new(model.Requisites.Number, model.Requisites.Name, model.Method.ToString().ToLowerInvariant(),
            [.. model.Chapters.Select(c => new ImportChapterDto(c.Index, c.Caption, c.Positions.Count(p => !p.IsInactive),
                cost ? new ImportCostDto(c.Total) : null))],
            positions.Count(p => !p.IsInactive), positions.Count(p => p.IsInactive),
            cost ? new ImportCostDto(model.Totals.Total, model.Totals.EstimateTotal) : null);

    /// <summary>
    /// Номер расчёта прежней редакции сметы, если он не совпадает с номером в файле (решение В-12 (а): «тот ли
    /// это расчёт» решает человек, отчёт только показывает расхождение). Прежняя — последняя редакция сметы,
    /// кроме созданной этим импортом; номер лежит у записи её импорта. Последняя заведена не импортом — сверять
    /// не с чем: номер более ранней редакции говорил бы о расчёте, который уже не прежний.
    /// </summary>
    private static async Task<string?> OtherCalculationAsync(PlanDbContext db, EstimateImport import, CancellationToken ct)
    {
        if (import is not { EstimateId: { } estimateId, LsrNumber: { } number }) return null;

        var own = import.Id.ToString();
        var keys = await db.EstimateRevisions.AsNoTracking()
            // Сравнение — по правилам C#: редакция без ключа импорта проходит, хотя в SQL «NULL <> x» ложно.
            .Where(r => r.EstimateId == estimateId && r.ImportKey != own)
            .OrderByDescending(r => r.Number).Select(r => r.ImportKey).Take(1).ToListAsync(ct);
        if (keys is not [{ } key] || !Guid.TryParse(key, out var previousId)) return null;

        var previous = await db.EstimateImports.AsNoTracking().Where(i => i.Id == previousId)
            .Select(i => i.LsrNumber).FirstOrDefaultAsync(ct);
        return previous is not null && !string.Equals(previous.Trim(), number.Trim(), StringComparison.OrdinalIgnoreCase)
            ? previous
            : null;
    }

    private static string StateCode(EstimateImportState state) => state switch
    {
        EstimateImportState.Parsing => "parsing",
        EstimateImportState.Parsed => "parsed",
        EstimateImportState.Rejected => "rejected",
        _ => "committed",
    };

    private static string? KindCode(GrandSmetaKind? kind) => kind switch
    {
        GrandSmetaKind.LocalEstimate => "lsr",
        GrandSmetaKind.VolumeStatement => "vor",
        GrandSmetaKind.Conjuncture => "ka",
        _ => null,
    };

    /// <summary>Почему документ не становится редакцией — одними словами в отчёте и в отказе создания редакции.</summary>
    internal static string NotEstimate(GrandSmetaKind kind) =>
        $"Это {GrandSmetaSchemas.KindName(kind)} — редакцией сметы {Its(kind)} не становится. Приложите файл к смете.";

    /// <summary>Местоимение по роду названия: «ведомость … — она», «конъюнктурный анализ — он».</summary>
    private static string Its(GrandSmetaKind kind) => kind == GrandSmetaKind.VolumeStatement ? "она" : "он";
}
