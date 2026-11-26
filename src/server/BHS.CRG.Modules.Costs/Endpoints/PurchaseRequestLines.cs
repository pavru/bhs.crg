using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Endpoints;

internal sealed record PurchaseRequestCreate(
    Guid ConstructionId, Guid? SectionId, Guid[]? PlanItemIds, PurchaseRequestLineBody?[]? Lines);

internal sealed record PurchaseRequestEdit(PurchaseRequestLineBody?[]? Lines);

internal sealed record PurchaseRequestReturn(string? Reason);

internal sealed record PurchaseRequestLineBody(Guid NomenclatureId, Guid? PlanItemId, decimal? Quantity, string? Unit, string? Note);

internal sealed class PurchaseRequestLines(IModuleCatalog catalog, IModulePlanItems planItems)
{
    public async Task<PurchaseRequestLineInput[]> ValidateAsync(
        PurchaseRequestLineBody?[]? body, Guid constructionId, IReadOnlyList<PurchaseRequestLine> stored,
        CancellationToken ct)
    {
        if (body is null) throw new InvalidRequestException("Передайте список строк заявки в поле lines.");
        var lines = body.Select((line, i) => Validate(line, i + 1)).ToArray();
        var positions = lines.Select((line, i) => (Number: i + 1, Position: (Guid?)line.NomenclatureId)).ToArray();
        var fresh = positions.Select(line => line.Position!.Value).Distinct()
            .Except(stored.Select(line => line.NomenclatureId)).ToArray();
        if (fresh.Length > 0)
        {
            var verdicts = await NewReferences.JudgeAsync(catalog, CostsDirectories.Nomenclature, fresh, ct)
                ?? throw new ConflictException(CostsDirectories.NoNomenclature("позиция номенклатуры обязательна в строке заявки"));
            var missing = NewReferences.Rows(positions, verdicts, NewReference.Missing);
            if (missing.Count > 0)
                throw new InvalidRequestException($"Позиции номенклатуры не найдены. Строки заявки: {string.Join(", ", missing)}.");
            NewReferences.EnsureNoneArchived(positions, verdicts, "позиция номенклатуры");
        }

        await PurchaseRequestEstimate.EnsurePlanItemsAsync(planItems, constructionId,
            lines.Select(line => line.PlanItemId).OfType<Guid>().Distinct().ToArray(), ct);
        return lines;
    }

    private static PurchaseRequestLineInput Validate(PurchaseRequestLineBody? line, int number)
    {
        if (line is null || line.NomenclatureId == Guid.Empty)
            throw new InvalidRequestException($"Строка {number}: выберите позицию номенклатуры.");
        if (line.Quantity is not { } quantity || quantity < 0 || quantity > PurchaseRequestLine.MaxQuantity
            || decimal.Round(quantity, PurchaseRequestLine.QuantityScale) != quantity)
            throw new InvalidRequestException(
                $"Строка {number}: укажите неотрицательное количество с точностью до {PurchaseRequestLine.QuantityScale} знаков.");
        var unit = line.Unit?.Trim();
        if (string.IsNullOrEmpty(unit) || unit.Length > PurchaseRequestLine.UnitLength)
            throw new InvalidRequestException($"Строка {number}: укажите единицу длиной до {PurchaseRequestLine.UnitLength} знаков.");
        var note = line.Note?.Trim();
        if (note?.Length > PurchaseRequestLine.NoteLength)
            throw new InvalidRequestException($"Строка {number}: примечание длиннее {PurchaseRequestLine.NoteLength} знаков.");
        return new PurchaseRequestLineInput(line.NomenclatureId, line.PlanItemId, quantity, unit, note);
    }
}
