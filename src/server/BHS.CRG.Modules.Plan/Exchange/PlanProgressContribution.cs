using System.Data;
using System.Globalization;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Exchange;

internal sealed class PlanProgressContribution(PlanDbContext db, IModuleContracts contracts, IModuleUnits units)
    : IProgressContribution
{
    private const string EstimateCode = "estimate";
    private const string EstimateTitle = "План по смете";
    private const string ScheduleCode = "schedule";
    private const string ScheduleTitle = "Срок по графику";
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public IReadOnlyList<string> AnswerPermissions { get; } = Array.AsReadOnly(new[] { PlanModule.EstimateRead, PlanModule.ScheduleRead });

    public async Task<ProgressContribution> ReadAsync(
        ModuleReader reader, Guid constructionId, Guid? sectionId, CancellationToken ct = default)
    {
        var strips = new List<ProgressStrip>();
        var estimate = await EstimateAsync(reader, constructionId, sectionId, strips, ct);
        var schedule = await ScheduleAsync(reader, constructionId, sectionId, ct);
        return new([estimate, schedule], strips, []);
    }

    private async Task<ProgressColumn> EstimateAsync(
        ModuleReader reader, Guid site, Guid? section, List<ProgressStrip> strips, CancellationToken ct)
    {
        if (ContractAnswer.Refusal<IReadOnlyList<ProgressItemValue>>(reader, PlanModule.EstimateRead) is { } refusal)
        {
            if (refusal is ContractAnswer<IReadOnlyList<ProgressItemValue>>.Hidden hidden)
                strips.Add(new(EstimateCode, hidden.Text, "hidden", Permission: hidden.Permission));
            return new(EstimateCode, EstimateTitle, ProgressColumnKind.Volume, refusal);
        }

        var path = reader.Allows(PlanModule.ModuleCode) && reader.Allows("core.constructions.read")
            ? $"/document-sets/{site}/estimates" : null;
        var provider = ((ModuleContractAnswer<IPlannedWorks>.Connected)contracts.Get<IPlannedWorks>()).Contract;
        // План раздела и предупреждение по всей стройке должны читать одну версию действующих редакций.
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var answer = await ReadPlanAsync(provider, reader, new(site, section), ct);
        var wholeItems = section.HasValue && answer.Undecided > 0
            ? (await ReadPlanAsync(provider, reader, new(site), ct)).Volumes.Items : null;
        var unmatched = path is not null && answer.Unmapped > 0
            ? await PlanRows.UnmatchedByRevision(db, site).ToListAsync(ct) : [];
        await tx.CommitAsync(ct);

        foreach (var basis in answer.Volumes.Basis)
            strips.Add(new($"estimate-basis-{basis.EstimateId}", $"План по: смета «{basis.EstimateName}» ред. {basis.Revision}",
                Link: path is null ? null : EstimateLink(path, basis.EstimateId, basis.RevisionId)));

        if (answer.Unmapped > 0)
            AddUnmatched(answer.Unmapped, unmatched, path, strips);
        if (wholeItems is not null)
            await AddUndistributedAsync(wholeItems, answer.Undecided, path, strips, ct);

        IReadOnlyList<ProgressItemValue> values = [.. answer.Volumes.Items.Select(item =>
            new ProgressItemValue(item.PlanItemId, new(Volume: item.HasVolume ? item.Volume : null)))];
        return new(EstimateCode, EstimateTitle, ProgressColumnKind.Volume, ContractAnswer.Shown(values));
    }

    private static async Task<(PlannedVolumes Volumes, int Unmapped, int Undecided)> ReadPlanAsync(
        IPlannedWorks provider, ModuleReader reader, PlannedWorksQuery query, CancellationToken ct)
    {
        if (await provider.ReadAsync(reader, query, ct) is ContractAnswer<PlannedWorksAnswer>.Shown { Value: var answer }
            && answer.Volumes is ContractAnswer<PlannedVolumes>.Shown { Value: var volumes })
            return (volumes, answer.Unmapped, answer.Undecided);
        throw new InvalidOperationException("Контракт «Плановые работы» не вернул объёмы читателю смет.");
    }

    private static string EstimateLink(string path, Guid estimateId, Guid revisionId) =>
        $"{path}/{estimateId}?revision={revisionId}";

    private static void AddUnmatched(int count, IReadOnlyList<PlanRows.UnmatchedRevision> revisions, string? path,
        List<ProgressStrip> strips)
    {
        static string Link(string path, PlanRows.UnmatchedRevision revision) =>
            $"{EstimateLink(path, revision.EstimateId, revision.RevisionId)}&queue=unmatched";

        var link = path is not null && revisions is [var only] ? Link(path, only) : null;
        strips.Add(new("estimate-unmapped", $"Строк сметы не сопоставлено: {count} — в таблице их нет", Link: link));
        if (path is null || revisions.Count < 2) return;

        foreach (var revision in revisions)
            strips.Add(new($"estimate-unmapped-{revision.RevisionId}",
                $"Смета «{revision.EstimateName}» ред. {revision.Number}: {revision.Count}", Link: Link(path, revision)));
    }

    private async Task AddUndistributedAsync(IReadOnlyList<PlannedItem> items, int undecided, string? path,
        List<ProgressStrip> strips, CancellationToken ct)
    {
        var byUnit = items.Where(item => item.Undistributed != 0m).GroupBy(item => item.UnitId).ToList();
        if (byUnit.Count == 0)
        {
            strips.Add(new("estimate-undistributed", $"Не распределено по разделам: строк {undecided} — план всей стройки", Link: path));
            return;
        }

        var labels = (await units.GetAsync([.. byUnit.Select(group => group.Key)], ct) ?? []).ToDictionary(unit => unit.Id, unit => unit.Name);
        foreach (var group in byUnit.OrderBy(group => labels.GetValueOrDefault(group.Key)).ThenBy(group => group.Key))
        {
            var quantity = group.Sum(item => item.Undistributed).ToString("#,0.######", Russian);
            var unit = labels.TryGetValue(group.Key, out var label) ? $" {label}" : "";
            var works = group.Count() == 1 ? "по одной работе" : $"по {group.Count()} работам";
            strips.Add(new($"estimate-undistributed-{group.Key}",
                $"Не распределено по разделам: {quantity}{unit} {works} — план всей стройки", Link: path));
        }
    }

    private async Task<ProgressColumn> ScheduleAsync(ModuleReader reader, Guid site, Guid? section, CancellationToken ct)
    {
        if (ContractAnswer.Refusal<IReadOnlyList<ProgressItemValue>>(reader, PlanModule.ScheduleRead) is { } refusal)
            return new(ScheduleCode, ScheduleTitle, ProgressColumnKind.Dates, refusal);

        var schedules = db.Schedules.AsNoTracking().Where(schedule => schedule.ConstructionId == site);
        var dates = await (from work in db.ScheduleWorks.AsNoTracking()
                           join schedule in schedules on work.ScheduleId equals schedule.Id
                           where section == null || work.SectionId == section
                           group work by work.PlanItemId into byItem
                           select new { Id = byItem.Key, Start = byItem.Min(work => work.Start), End = byItem.Max(work => work.End) })
            .ToListAsync(ct);
        IReadOnlyList<ProgressItemValue> values = [.. dates.Select(date => new ProgressItemValue(date.Id, new(Start: date.Start, End: date.End)))];
        return new(ScheduleCode, ScheduleTitle, ProgressColumnKind.Dates, ContractAnswer.Shown(values));
    }
}
