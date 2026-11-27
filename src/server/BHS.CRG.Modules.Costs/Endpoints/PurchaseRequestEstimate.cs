using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Endpoints;

internal sealed record PurchaseRequestSource(Guid ConstructionId, Guid? SectionId, Guid[]? PlanItemIds);

internal sealed record PurchaseRequestSuggestionLine(
    Guid NomenclatureId, Guid? PlanItemId, string? Unit, decimal EstimateQuantity,
    decimal? CompatibleIssuedQuantity, decimal Quantity, bool IssuedExceeds, bool UnitMismatch);

internal sealed record PurchaseRequestSuggestion(
    IReadOnlyList<PurchaseRequestSuggestionLine> Lines, int UnmatchedIssued, int UnmatchedEstimate,
    int UndecidedEstimate, string Selection, IReadOnlyList<string> Warnings);

internal sealed class PurchaseRequestEstimate(
    IModuleContracts contracts, IModuleReaders readers, IModuleConstructions constructions,
    IModulePlanItems planItems, CostsDbContext db)
{
    public async Task<ContractAnswer<PlannedWorksAnswer>> ReadAsync(PurchaseRequestSource source, CancellationToken ct)
    {
        if (contracts.Get<IPlannedWorks>() is not ModuleContractAnswer<IPlannedWorks>.Connected provider)
            throw new ConflictException("Для создания заявки нужен модуль «Планирование».");

        var hasSection = source.SectionId is not null;
        if (hasSection ? source.PlanItemIds is not null : source.PlanItemIds is not { Length: > 0 })
            throw new InvalidRequestException("Выберите позиции перечня или один раздел стройки.");

        var query = new PlannedWorksQuery(source.ConstructionId, source.SectionId,
            hasSection ? null : source.PlanItemIds!.Distinct().ToArray());
        var answer = await provider.Contract.ReadAsync(await readers.ForRequestAsync(ct), query, ct);
        if (answer is not ContractAnswer<PlannedWorksAnswer>.Shown { Value.Materials: ContractAnswer<PlannedMaterials>.Shown })
            return answer;

        var construction = (await constructions.ListAsync(ct)).FirstOrDefault(site => site.Id == query.ConstructionId)
            ?? throw new InvalidRequestException("Выбранной стройки нет.");
        if (query.SectionId is { } section && construction.Sections.All(item => item.Id != section))
            throw new InvalidRequestException("Выбранный раздел не принадлежит этой стройке.");
        if (query.PlanItemIds is { } ids)
            await EnsurePlanItemsAsync(planItems, query.ConstructionId, ids, ct);
        return answer;
    }

    internal static async Task EnsurePlanItemsAsync(
        IModulePlanItems planItems, Guid constructionId, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        var found = await planItems.GetAsync(ids, ct);
        if (found.Count != ids.Count || found.Any(item => item.Key.ConstructionId != constructionId))
            throw new InvalidRequestException("Позиции перечня должны существовать и относиться к выбранной стройке.");
    }

    public async Task<PurchaseRequestSuggestion> SuggestAsync(
        PurchaseRequestSource source, PlannedWorksAnswer plan, PlannedMaterials materials, CancellationToken ct)
    {
        // Строки и число несопоставленных накладных должны относиться к одному снимку.
        await using var snapshot = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var issued = await IssuedMaterials.ReadAsync(db, source.ConstructionId, ct);
        return Calculate(source, plan.Undecided, materials, issued);
    }

    internal static PurchaseRequestSuggestion Calculate(
        PurchaseRequestSource source, int undecided, PlannedMaterials materials, IssuedMaterialsReport issued)
    {
        var issuedByMaterial = issued.Items.ToLookup(line => line.NomenclatureId);
        var lines = new List<PurchaseRequestSuggestionLine>();

        foreach (var material in materials.Lines.GroupBy(line => line.NomenclatureId))
        {
            var delivered = issuedByMaterial[material.Key].ToLookup(line => PurchaseRequestLine.ComparableUnit(line.Unit));
            var units = material.ToLookup(line => PurchaseRequestLine.ComparableUnit(line.Unit));
            foreach (var group in units)
            {
                if (group.Key is null)
                {
                    foreach (var line in group)
                        lines.Add(Suggestion(material.Key, line.PlanItemId, null, line.Quantity, null, true));
                    continue;
                }

                var sources = group.Select(line => line.PlanItemId).Distinct().Take(2).ToArray();
                var compatible = delivered[group.Key].ToList();
                var incompatible = delivered.Any(unit => unit.Key != group.Key);
                decimal? quantity = compatible.Count > 0 ? compatible.Sum(line => line.Quantity) : incompatible ? null : 0;
                lines.Add(Suggestion(material.Key, sources.Length == 1 ? sources[0] : null, group.Key,
                    group.Sum(line => line.Quantity), quantity, units.Count > 1 || incompatible));
            }
        }

        return new PurchaseRequestSuggestion(lines, issued.UnmatchedLines, materials.Unmatched, undecided,
            source.SectionId is null ? "planItems" : "constructionSection",
            [
                "Отпуск посчитан по всей стройке, без распределения между позициями перечня и разделами.",
                "Смета ограничена выбором. Предложение требует ручной проверки остатка для выбранных позиций или раздела.",
                "Другие заявки не резервируют количество и не участвуют в расчёте. До нового отпуска предложение может повториться.",
            ]);
    }

    private static PurchaseRequestSuggestionLine Suggestion(
        Guid material, Guid? source, string? unit, decimal planned, decimal? issued, bool mismatch) =>
        new(material, source, unit, planned, issued, Math.Max(0, planned - (issued ?? 0)), issued > planned, mismatch);
}
