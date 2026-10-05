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
/// <para><b>Внутренняя модель модуля.</b> Наружу — контрактом для других модулей — она выйдет с первым
/// настоящим потребителем (решение 28.09.2026: потребитель этапа 6 был бы придуманным). Здесь она
/// нужна самому модулю: ею вычисляется признак «сопоставлен».</para>
///
/// <para>⚠️ <b>Строка без позиции номенклатуры в перечень не попадает — и названа числом.</b> Пустить
/// её в перечень нечем: материал без ссылки на справочник не свести ни с документом качества, ни с
/// тем, что списал монтажник. Но и промолчать о ней нельзя: перечень без оговорки читался бы как
/// «выдано только это», а выдано больше. Поэтому одно без другого не отдаётся — счётчик лежит в том же
/// ответе, что и перечень.</para>
///
/// <para>Черновик не считается ни там, ни там: его строки ещё никому не выданы.</para>
/// </summary>
public static class IssuedMaterials
{
    public static async Task<IssuedMaterialsReport> ReadAsync(
        CostsDbContext db, Guid constructionId, CancellationToken ct)
    {
        var issued =
            from line in db.WaybillLines.AsNoTracking()
            join waybill in db.Waybills.AsNoTracking() on line.WaybillId equals waybill.Id
            where waybill.State == WaybillState.Posted && waybill.ConstructionId == constructionId
            select new { line.WaybillId, line.NomenclatureId, line.Unit, line.Quantity, waybill.IssuedOn };

        // Проведённая накладная обязана иметь дату и количество в каждой строке — это проверяет
        // проведение. Отбор здесь на случай строки, записанной мимо него: без даты и количества
        // считать нечего, и молча подставить ноль значило бы показать «выдано 0».
        var items = await issued
            .Where(l => l.NomenclatureId != null && l.Quantity != null && l.IssuedOn != null)
            .GroupBy(l => new { l.NomenclatureId, l.Unit })
            .Select(g => new IssuedMaterial(
                g.Key.NomenclatureId!.Value,
                g.Key.Unit,
                g.Sum(l => l.Quantity!.Value),
                g.Min(l => l.IssuedOn!.Value),
                g.Max(l => l.IssuedOn!.Value),
                g.Select(l => l.WaybillId).Distinct().Count()))
            .ToListAsync(ct);

        var unmatched = issued.Where(l => l.NomenclatureId == null);

        return new IssuedMaterialsReport(
            items,
            await unmatched.CountAsync(ct),
            await unmatched.Select(l => l.WaybillId).Distinct().CountAsync(ct));
    }
}
