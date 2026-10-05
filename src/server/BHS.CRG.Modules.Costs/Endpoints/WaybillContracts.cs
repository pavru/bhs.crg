using System.Text.Json;
using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Набор строк накладной целиком — как у счёта: адрес заменяет состояние, а не дополняет.</summary>
public sealed record WaybillLinesRequest(IReadOnlyList<JsonElement>? Lines);

public sealed record WaybillLineView(
    Guid Id,
    int Ordinal,
    Guid? NomenclatureId,
    string? NomenclatureName,
    // Ссылка есть, а позиции в справочнике нет. Отдельным признаком, а не пустым названием: «позицию
    // удалили» и «позицию не выбрали» чинятся по-разному.
    bool NomenclatureLost,
    string? SourceText,
    string? Unit,
    decimal? Quantity,
    string? Note);

/// <summary>
/// Счётчики строк. «Не сопоставлено» — число, которое обязано быть на виду (ТЗ COST-17): столько
/// строк этой накладной в «материалы на объекте» не попадает.
/// </summary>
public sealed record WaybillLineTotals(int Count, int Unmatched);

public sealed record WaybillView(
    Guid Id,
    string? Number,
    DateOnly? IssuedOn,
    string? Warehouse,
    Guid? ConstructionId,
    string? ConstructionName,
    bool ConstructionLost,
    string? ReceivedBy,
    string? Note,
    string State,
    DateTimeOffset? PostedAt,
    IReadOnlyList<WaybillLineView> Lines,
    WaybillLineTotals Totals);

public sealed record WaybillListItem(
    Guid Id,
    string? Number,
    DateOnly? IssuedOn,
    string? Warehouse,
    Guid? ConstructionId,
    string? ConstructionName,
    string State,
    int Lines,
    int Unmatched);

public sealed record IssuedMaterialView(
    Guid NomenclatureId, string? Name, string? Unit, decimal Quantity, DateOnly First, DateOnly Last, int Waybills);

/// <summary>
/// Перечень отпущенного на стройку и оговорка к нему одним ответом: перечень без счётчика читался бы
/// как «выдано только это».
/// </summary>
public sealed record IssuedMaterialsView(
    Guid ConstructionId, IReadOnlyList<IssuedMaterialView> Items, int UnmatchedLines, int UnmatchedWaybills);

/// <summary>Разбор присланного — с отказами, называющими поле (тот же приём, что у счёта).</summary>
public static class WaybillRequests
{
    private const string NomenclatureWhy =
        "Позицию выбирают из справочника номенклатуры, а не вписывают наименованием: без ссылки " +
        "материал не попадёт в перечень отпущенного на стройку. Наименование из накладной присылайте " +
        "в «sourceText» — оно останется цитатой, а строка будет считаться несопоставленной.";

    public static WaybillHeader Header(JsonElement body)
    {
        CostsValues.EnsureObject(body, "Накладная", "шапки");
        return new WaybillHeader(
            Number: CostsValues.Text(body, "number", "Номер", Waybill.NumberLength),
            IssuedOn: CostsValues.Date(body, "issuedOn"),
            Warehouse: CostsValues.Text(body, "warehouse", "Склад", Waybill.NameLength),
            ConstructionId: Identifier(body, "construction", "Стройка"),
            ReceivedBy: CostsValues.Text(body, "receivedBy", "Получил", Waybill.NameLength),
            Note: CostsValues.Text(body, "note", "Примечание"));
    }

    public static Guid? LineId(JsonElement line, int number)
    {
        CostsValues.EnsureObject(line, $"Строка {number}", "строки");
        return Identifier(line, "id", $"Строка {number}: идентификатор");
    }

    public static WaybillLineValues Line(JsonElement line, int number)
    {
        CostsValues.EnsureObject(line, $"Строка {number}", "строки");
        return new WaybillLineValues(
            NomenclatureId: Nomenclature(line, $"Позиция номенклатуры, строка {number}"),
            SourceText: CostsValues.Text(line, "sourceText", $"Наименование в накладной, строка {number}"),
            Unit: CostsValues.Text(line, "unit", $"Единица измерения, строка {number}", WaybillLine.UnitLength),
            Quantity: CostsValues.Quantity(line, "quantity", $"Количество, строка {number}"),
            Note: CostsValues.Text(line, "note", $"Примечание, строка {number}"));
    }

    public static Guid? Nomenclature(JsonElement source, string label) =>
        CostsValues.Reference(source, "nomenclature", NomenclatureWhy, label);

    private static Guid? Identifier(JsonElement source, string key, string label) =>
        CostsValues.Value(source, key) switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } value when Guid.TryParse(value.GetString(), out var id) => id,
            var other => throw CostsValues.Wrong(label, other, "строку-идентификатор либо ничего"),
        };
}

public static class WaybillViews
{
    public static WaybillListItem Item(Waybill waybill, ModuleConstruction? site, int lines, int unmatched) => new(
        waybill.Id, waybill.Number, waybill.IssuedOn, waybill.Warehouse, waybill.ConstructionId, site?.Name,
        waybill.State.ToString(), lines, unmatched);

    /// <param name="names">Названия позиций; <c>null</c> — справочника номенклатуры в системе нет, и
    /// «позиция потеряна» сказать не о чём (см. <c>InvoiceEndpoints.NomenclatureNamesAsync</c>).</param>
    public static WaybillView Full(Waybill waybill, IReadOnlyList<WaybillLine> lines, ModuleConstruction? site,
        IReadOnlyDictionary<Guid, string?>? names) => new(
        waybill.Id, waybill.Number, waybill.IssuedOn, waybill.Warehouse,
        waybill.ConstructionId, site?.Name, ConstructionLost: waybill.ConstructionId is not null && site is null,
        waybill.ReceivedBy, waybill.Note, waybill.State.ToString(), waybill.PostedAt,
        [.. lines.OrderBy(l => l.Ordinal).Select(l => new WaybillLineView(
            l.Id, l.Ordinal, l.NomenclatureId,
            l.NomenclatureId is { } id && names is not null ? names.GetValueOrDefault(id) : null,
            NomenclatureLost: l.NomenclatureId is { } position && names is not null && !names.ContainsKey(position),
            l.SourceText, l.Unit, l.Quantity, l.Note))],
        new WaybillLineTotals(lines.Count, lines.Count(l => l.NomenclatureId is null)));
}
