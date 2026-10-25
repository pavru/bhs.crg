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
        var unparsed = EstimateImportJson.Unparsed(import);
        if (import.State != EstimateImportState.Rejected && import.Kind is { } kind and not GrandSmetaKind.LocalEstimate)
            marks.Add(new("notEstimate",
                $"Это {GrandSmetaSchemas.KindName(kind)} — редакцией сметы {Its(kind)} не становится. Приложите файл к смете."));
        if (model is not null)
        {
            if (unparsed.Count > 0)
                marks.Add(new("partial",
                    $"Разобрано {model.Positions.Count()} из {model.PositionsInFile}, {unparsed.Count} не разобрано — " +
                    "редакция из такого файла создаётся только с вашего согласия."));
            if (model.Positions.Any(p => p.TotalSource == TotalSource.ComputedBySystem))
                marks.Add(new("computedTotals",
                    "Файл сохранён без итогов — итоги позиций посчитаны системой по данным файла."));
            if (await OtherCalculationAsync(db, import, ct) is { } previous)
                marks.Add(new("otherCalculation",
                    $"Номер расчёта в файле — «{import.LsrNumber}», а у прежней редакции сметы — «{previous}». Проверьте, " +
                    "что это тот же расчёт: иначе стоит импортировать его в новую смету."));
        }

        var target = new EstimateImportTargetDto(
            import.EstimateId,
            import.EstimateId is { } estimateId
                ? await db.Estimates.AsNoTracking().Where(e => e.Id == estimateId).Select(e => e.Name).FirstOrDefaultAsync(ct)
                : null,
            import.NewEstimateName is { } name
                ? new NewEstimateDto(name, import.NewEstimateSectionId, EstimateViews.KindCode(import.NewEstimateKind ?? EstimateKind.Main))
                : null);

        return new EstimateImportDto(
            import.Id, import.ConstructionId, target, import.FileName, StateCode(import.State), KindCode(import.Kind),
            import.Kind is { } k ? GrandSmetaSchemas.KindName(k) : null, import.ProgramVersion, import.SchemaVersion,
            progress,
            import is { RefusalCode: { } code, RefusalText: { } text } ? new EstimateImportRefusalDto(code, text) : null,
            model?.Positions.Count(), model?.PositionsInFile,
            [.. unparsed.Select(u => new UnparsedPositionDto(u.Place, u.Line, u.Reason))],
            model is null ? null : Summary(model, cost),
            marks, import.RevisionId, import.CreatedAt, import.CreatedByName,
            found.Version.ToString(CultureInfo.InvariantCulture), hidden);
    }

    public static NotFoundException NotFound() => new("Импорт не найден — возможно, его отменили.");

    private static ImportSummaryDto Summary(ParsedLocalEstimate model, bool cost) =>
        new(model.Requisites.Number, model.Requisites.Name, model.Method.ToString().ToLowerInvariant(),
            [.. model.Chapters.Select(c => new ImportChapterDto(c.Index, c.Caption, c.Positions.Count,
                cost ? new ImportCostDto(c.Total) : null))],
            model.Positions.Count(), model.Positions.Count(p => p.IsInactive),
            cost ? new ImportCostDto(model.Totals.Total, model.Totals.EstimateTotal) : null);

    /// <summary>
    /// Номер расчёта прежней редакции сметы, если он не совпадает с номером в файле (решение В-12 (а): «тот ли
    /// это расчёт» решает человек, отчёт только показывает расхождение). Прежняя — последняя редакция, созданная
    /// импортом; номер лежит у записи её импорта.
    /// </summary>
    private static async Task<string?> OtherCalculationAsync(PlanDbContext db, EstimateImport import, CancellationToken ct)
    {
        if (import is not { EstimateId: { } estimateId, LsrNumber: { } number }) return null;

        var own = import.Id.ToString();
        var keys = await db.EstimateRevisions.AsNoTracking()
            .Where(r => r.EstimateId == estimateId && r.ImportKey != null && r.ImportKey != own)
            .OrderByDescending(r => r.Number).Select(r => r.ImportKey!).Take(1).ToListAsync(ct);
        if (keys is not [var key] || !Guid.TryParse(key, out var previousId)) return null;

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

    /// <summary>Местоимение по роду названия: «ведомость … — она», «конъюнктурный анализ — он».</summary>
    private static string Its(GrandSmetaKind kind) => kind == GrandSmetaKind.VolumeStatement ? "она" : "он";
}
