using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>Материал, отпущенный на стройку: позиция, единица и сколько выдано.</summary>
/// <param name="Waybills">В скольких накладных позиция встречается.</param>
public sealed record IssuedMaterial(
    Guid NomenclatureId, string? Unit, decimal Quantity, DateOnly First, DateOnly Last, int Waybills);

/// <summary>
/// Перечень отпущенного на стройку — и то, что в него НЕ попало.
/// </summary>
/// <param name="UnmatchedLines">Строки проведённых накладных без позиции номенклатуры.</param>
/// <param name="UnmatchedWaybills">В скольких накладных такие строки лежат.</param>
public sealed record IssuedMaterialsReport(
    IReadOnlyList<IssuedMaterial> Items, int UnmatchedLines, int UnmatchedWaybills);

/// <summary>
/// «Материалы на объекте»: что выдано на стройку проведёнными накладными (ТЗ COST-17; задача D1
/// этапа 2, issue #1083).
///
/// <para>Общий расчёт для экрана накладных и поставщика межмодульного контракта
/// «Материалы на объекте» (work-09).</para>
///
/// <para>⚠️ <b>Строка без позиции номенклатуры в перечень не попадает — и названа числом.</b> Пустить
/// её в перечень нечем: материал без ссылки на справочник не свести ни с документом качества, ни с
/// тем, что списал монтажник. Число таких строк дополняет полный отчёт о выданных материалах.</para>
///
/// <para>Черновик не считается ни там, ни там: его строки ещё никому не выданы.</para>
/// </summary>
public static class IssuedMaterials
{
    internal static Task<List<Guid>> ReadNomenclatureIdsAsync(
        CostsDbContext db, Guid constructionId, CancellationToken ct) =>
        Matched(PostedLines(db, constructionId))
            .Select(item => item.Line.NomenclatureId!.Value)
            .Distinct()
            .ToListAsync(ct);

    public static async Task<IssuedMaterialsReport> ReadAsync(
        CostsDbContext db, Guid constructionId, CancellationToken ct)
    {
        var issued = PostedLines(db, constructionId);
        var items = await Matched(issued)
            .GroupBy(item => new { item.Line.NomenclatureId, item.Line.Unit })
            .Select(g => new IssuedMaterial(
                g.Key.NomenclatureId!.Value,
                g.Key.Unit,
                g.Sum(item => item.Line.Quantity!.Value),
                g.Min(item => item.Waybill.IssuedOn!.Value),
                g.Max(item => item.Waybill.IssuedOn!.Value),
                g.Select(item => item.Line.WaybillId).Distinct().Count()))
            .ToListAsync(ct);

        var unmatched = issued.Where(item => item.Line.NomenclatureId == null);

        return new IssuedMaterialsReport(
            items,
            await unmatched.CountAsync(ct),
            await unmatched.Select(item => item.Line.WaybillId).Distinct().CountAsync(ct));
    }

    private static IQueryable<IssuedLine> PostedLines(CostsDbContext db, Guid constructionId) =>
        from line in db.WaybillLines.AsNoTracking()
        join waybill in db.Waybills.AsNoTracking() on line.WaybillId equals waybill.Id
        where waybill.State == WaybillState.Posted && waybill.ConstructionId == constructionId
        select new IssuedLine { Line = line, Waybill = waybill };

    // Проверка проведения требует дату и количество. Общий отбор исключает неполные строки,
    // внесённые в обход проведения, из полного отчёта и из подсказки.
    private static IQueryable<IssuedLine> Matched(IQueryable<IssuedLine> issued) =>
        issued.Where(item => item.Line.NomenclatureId != null && item.Line.Quantity != null && item.Waybill.IssuedOn != null);

    private sealed class IssuedLine
    {
        public required WaybillLine Line { get; init; }
        public required Waybill Waybill { get; init; }
    }
}
