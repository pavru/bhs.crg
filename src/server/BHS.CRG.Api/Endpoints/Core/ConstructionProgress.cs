using System.Text.Json.Serialization;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Endpoints.Core;

internal sealed class ConstructionProgress(
    IModuleContracts contracts, IModulePlanItems items, IModuleWorkTypes workTypes, IModuleUnits units)
{
    private const string Fact = "fact";
    private const string FactTitle = "Факт по учёту";

    public async Task<ConstructionProgressResponse> ReadAsync(
        ModuleReader reader, Guid site, Guid? section, CancellationToken ct)
    {
        var columns = new List<ProgressColumn>();
        var strips = new List<ProgressStrip>();
        var planItemIds = new HashSet<Guid>();
        // Последовательно: поставщики могут читать справочники одним контекстом ядра.
        foreach (var provider in contracts.GetAll<IProgressContribution>())
        {
            var contribution = await provider.Contract.ReadAsync(reader, site, section, ct);
            var prefix = Uri.EscapeDataString(provider.Module);
            var localCodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in contribution.Columns)
            {
                if (!localCodes.Add(column.Code))
                    throw new InvalidOperationException($"Модуль «{provider.Module}» повторил код колонки «{column.Code}» в «Ходе работ».");
                columns.Add(column with { Code = $"{prefix}:{column.Code}" });
            }
            strips.AddRange(contribution.Strips);
            planItemIds.UnionWith(contribution.PlanItemIds);
        }

        var diagnostics = await AddFactAsync(reader, new ActualWorksQuery(site, section), columns, strips, ct);

        var values = columns.SelectMany(c => ShownValues(c).Select(v => (c, v)))
            .ToLookup(pair => pair.v.PlanItemId, pair => pair);
        planItemIds.UnionWith(values.Select(g => g.Key));
        var rows = await RowsAsync(site, planItemIds, values, ct);
        var costs = await ReadCostsAsync(reader, site, section, ct);
        var partial = diagnostics?.IsPartial() == true;
        return new(site, section, columns.Select(column => ColumnView(column, partial)).ToList(), rows, strips, costs, diagnostics);
    }

    private async Task<ActualWorksDiagnostics?> AddFactAsync(ModuleReader reader, ActualWorksQuery query, List<ProgressColumn> columns,
        List<ProgressStrip> strips, CancellationToken ct)
    {
        var provider = contracts.Get<IActualWorks>();
        if (provider is ModuleContractAnswer<IActualWorks>.NotConnected missing)
        {
            strips.Add(new(Fact, "Учёт работ не подключён", "not-connected", ProviderTitle: missing.ProviderTitle));
            return null;
        }

        var answer = await ((ModuleContractAnswer<IActualWorks>.Connected)provider).Contract.ReadAsync(reader, query, ct);
        switch (answer)
        {
            case ContractAnswer<ActualWorksAnswer>.Shown(var facts):
                var values = facts.Items.GroupBy(row => row.PlanItemId)
                    .Select(g => new ProgressItemValue(g.Key, new(Volume: g.Sum(row => row.Volume))))
                    .ToArray();
                columns.Add(new(Fact, FactTitle, ProgressColumnKind.Volume,
                    ContractAnswer.Shown<IReadOnlyList<ProgressItemValue>>(values)));
                AddFactStrips(facts.Diagnostics, strips);
                return facts.Diagnostics;
            case ContractAnswer<ActualWorksAnswer>.Hidden(var permission):
                columns.Add(new(Fact, FactTitle, ProgressColumnKind.Volume,
                    ContractAnswer.Hidden<IReadOnlyList<ProgressItemValue>>(permission)));
                break;
            case ContractAnswer<ActualWorksAnswer>.SystemRefused(var reason):
                columns.Add(new(Fact, FactTitle, ProgressColumnKind.Volume,
                    ContractAnswer.SystemRefused<IReadOnlyList<ProgressItemValue>>(reason)));
                break;
        }
        return null;
    }

    private static void AddFactStrips(ActualWorksDiagnostics diagnostics, List<ProgressStrip> strips)
    {
        var partial = diagnostics.IsPartial();
        if (!partial && diagnostics.Unsubmitted == 0) return;

        // У этого адреса один отбор факта: раздел. Охват счётчиков поставщика всегда шире него.
        strips.Add(new("fact-scope", "Диагностика факта по всей стройке за весь период", partial ? "partial" : "ok"));
        if (diagnostics.AwaitingAcceptance > 0)
            strips.Add(new("fact-pending", $"Отчётов ожидают приёмки: {diagnostics.AwaitingAcceptance}", "partial"));
        if (diagnostics.WithoutPlanItem > 0)
            strips.Add(new("fact-without-plan-item", $"Строк без корректной позиции перечня: {diagnostics.WithoutPlanItem}", "partial"));
        if (diagnostics.Unsubmitted > 0)
            strips.Add(new("fact-unsubmitted", $"Неподанные участники по бригадным группам: {diagnostics.Unsubmitted}"));

        foreach (var item in diagnostics.CrewExclusions)
        {
            var (code, reason) = item.Reason switch
            {
                CrewFactExclusion.NotApproved => ("not-approved", "итог не утверждён"),
                CrewFactExclusion.Stale => ("stale", "итог устарел"),
                CrewFactExclusion.AwaitingAcceptance => ("awaiting-acceptance", "отчёты ждут приёмки"),
                CrewFactExclusion.InvalidKey => ("invalid-key", "негодная позиция перечня или единица"),
                CrewFactExclusion.MultipleKeys => ("multiple-keys", "разные позиции, разделы или единицы"),
                _ => throw new InvalidOperationException($"Неизвестная причина исключения бригадного итога: {item.Reason}."),
            };
            strips.Add(new($"fact-crew-{code}", $"Бригадных групп — {reason}: {item.Groups}", "partial"));
        }
    }

    private async Task<ProgressCosts> ReadCostsAsync(ModuleReader reader, Guid site, Guid? section, CancellationToken ct)
    {
        var provider = contracts.Get<IActualCosts>();
        if (provider is ModuleContractAnswer<IActualCosts>.NotConnected missing)
            return new("not-connected", missing.Text);

        var answer = await ((ModuleContractAnswer<IActualCosts>.Connected)provider).Contract.ReadAsync(reader, site, section, ct);
        return answer switch
        {
            ContractAnswer<ActualCostsTotals>.Shown(var totals) => new("ok", Totals: totals),
            ContractAnswer<ActualCostsTotals>.Hidden hidden => new("hidden", hidden.Text, hidden.Permission),
            ContractAnswer<ActualCostsTotals>.SystemRefused refused => new("not-available", refused.Text),
            _ => throw new InvalidOperationException("Неизвестный ответ контракта затрат."),
        };
    }

    private async Task<IReadOnlyList<ProgressRow>> RowsAsync(Guid site, IReadOnlyCollection<Guid> ids,
        ILookup<Guid, (ProgressColumn c, ProgressItemValue v)> values, CancellationToken ct)
    {
        if (ids.Count == 0) return [];

        var groups = (await items.GetAsync(ids, ct)).Where(item => item.Key.ConstructionId == site)
            .GroupBy(item => (item.Key.WorkTypeId, item.Key.UnitId)).ToList();
        if (groups.Count == 0) return [];

        var workTypeIds = groups.Select(g => g.Key.WorkTypeId).Distinct().ToArray();
        var unitIds = groups.Select(g => g.Key.UnitId).Distinct().ToArray();
        var typeNames = (await workTypes.GetAsync(workTypeIds, ct) ?? []).ToDictionary(w => w.Id, w => w.DisplayName);
        var unitNames = (await units.GetAsync(unitIds, ct) ?? []).ToDictionary(u => u.Id, u => u.Name);

        return groups.Select(group => new ProgressRow(group.Key.WorkTypeId, group.Key.UnitId,
                typeNames.GetValueOrDefault(group.Key.WorkTypeId), unitNames.GetValueOrDefault(group.Key.UnitId),
                group.SelectMany(item => values[item.Id]).GroupBy(pair => pair.c.Code)
                    .ToDictionary(g => g.Key, g => Aggregate(g.First().c.Kind, g.Select(pair => pair.v.Value).ToArray()))))
            .OrderBy(row => row.WorkTypeName).ThenBy(row => row.UnitName)
            .ThenBy(row => row.WorkTypeId).ThenBy(row => row.UnitId).ToList();
    }

    private static ProgressValue Aggregate(ProgressColumnKind kind, IReadOnlyList<ProgressValue> values) =>
        kind == ProgressColumnKind.Dates
            ? new(Start: values.Min(v => v.Start), End: values.Max(v => v.End))
            : new(Volume: values.Any(v => v.Volume.HasValue) ? values.Sum(v => v.Volume) : null);

    private static IReadOnlyList<ProgressItemValue> ShownValues(ProgressColumn column) =>
        column is { Values: ContractAnswer<IReadOnlyList<ProgressItemValue>>.Shown shown, UnavailableReason: null }
            ? shown.Value : [];

    private static ProgressColumnView ColumnView(ProgressColumn column, bool partialFact) => column.Values switch
    {
        ContractAnswer<IReadOnlyList<ProgressItemValue>>.Hidden hidden =>
            new(column.Code, column.Title, column.Kind, "hidden", hidden.Permission, hidden.Text),
        ContractAnswer<IReadOnlyList<ProgressItemValue>>.SystemRefused refused =>
            new(column.Code, column.Title, column.Kind, "not-available", Text: refused.Text),
        _ when column.UnavailableReason is { } reason =>
            new(column.Code, column.Title, column.Kind, "not-available", Text: reason),
        _ => new(column.Code, column.Title, column.Kind, column.Code == Fact && partialFact ? "partial" : "ok"),
    };
}

internal sealed record ConstructionProgressResponse(
    Guid ConstructionId, Guid? SectionId, IReadOnlyList<ProgressColumnView> Columns, IReadOnlyList<ProgressRow> Rows,
    IReadOnlyList<ProgressStrip> Strips, ProgressCosts Costs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActualWorksDiagnostics? FactDiagnostics);

internal sealed record ProgressColumnView(
    string Code, string Title, ProgressColumnKind Kind, string State, string? Permission = null, string? Text = null);

internal sealed record ProgressRow(
    Guid WorkTypeId, Guid UnitId, string? WorkTypeName, string? UnitName, IReadOnlyDictionary<string, ProgressValue> Cells);

internal sealed record ProgressCosts(string State, string? Text = null, string? Permission = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ActualCostsTotals? Totals = null);
