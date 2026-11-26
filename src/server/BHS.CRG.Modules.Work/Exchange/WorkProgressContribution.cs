using System.Globalization;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Labor;

namespace BHS.CRG.Modules.Work.Exchange;

internal sealed class WorkProgressContribution(LaborAnalytics analytics, IModuleClock clock, IModuleWorkTypes types,
    IModuleUnits units) : IProgressContribution
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly ProgressContribution Empty = new([], [], []);

    public IReadOnlyList<string> AnswerPermissions { get; } =
        Array.AsReadOnly(new[] { WorkModule.ReportReview, WorkModule.ReportRead });

    public Task<ProgressContribution> ReadAsync(ModuleReader reader, Guid constructionId, Guid? sectionId,
        CancellationToken ct = default) => ReadForPeriodAsync(reader, constructionId, sectionId, null, ct);

    public async Task<ProgressContribution> ReadForPeriodAsync(ModuleReader reader, Guid constructionId, Guid? sectionId,
        DateOnly? laborMonth, CancellationToken ct = default)
    {
        if (reader.IsSystem || !AnswerPermissions.Any(reader.Allows)) return Empty;
        var today = laborMonth ?? await clock.TodayOnSiteAsync(constructionId, ct);
        if (today is not { } day) return Empty;
        var from = new DateOnly(day.Year, day.Month, 1);
        var to = new DateOnly(day.Year, day.Month, DateTime.DaysInMonth(day.Year, day.Month));
        var result = await analytics.CompareProgressAsync(reader, constructionId, sectionId, from, to, ct);
        if (result is null) return Empty;

        var detail = await DetailsAsync(result, ct);
        var text = result.Reason ?? $"По смете {Hours(result.EstimatedHours, "чел.-ч")} — фактически {Hours(result.ActualHours, "ч")}";
        if (result.Distributed && result.State == LaborStates.Ok) text += " · расчётная";
        return new([], [new("work-labor", text, result.State, Permission: result.Permission, Month: from, Labor: detail)], []);
    }

    private async Task<ProgressLabor?> DetailsAsync(LaborComparison comparison, CancellationToken ct)
    {
        if (comparison.OffEstimate is not { } off) return null;
        var work = off.Works;
        var names = work.Count == 0 ? [] : await types.GetAsync([.. work.Select(w => w.WorkTypeId).Distinct()], ct) ?? [];
        var measures = work.Count == 0 ? [] : await units.GetAsync([.. work.Select(w => w.UnitId).Distinct()], ct) ?? [];
        var byType = names.ToDictionary(t => t.Id, t => t.DisplayName);
        var byUnit = measures.ToDictionary(u => u.Id, u => u.Name);
        var incomplete = comparison.Diagnostics is { } diagnostics &&
            (diagnostics.ExcludedCrewGroups > 0 || diagnostics.MissingHoursReports > 0 || diagnostics.UnknownCrewShares > 0);
        return new(comparison.EstimatedHours, comparison.ActualHours, comparison.Distributed, off.Hours,
            [.. work.Select(w => new ProgressLaborWork(byType.GetValueOrDefault(w.WorkTypeId), byUnit.GetValueOrDefault(w.UnitId), w.Volume))],
            comparison.UnallocatedHours, incomplete);
    }

    private static string Hours(decimal? value, string unit) => value is { } hours
        ? $"{hours.ToString("0.##", Russian)} {unit}" : "нет значения";
}
