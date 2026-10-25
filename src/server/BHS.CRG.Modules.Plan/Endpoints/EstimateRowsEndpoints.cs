using System.Globalization;
using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Matching;
using BHS.CRG.Modules.Ports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Строки редакции сметы — страницами, с ресурсами (change <c>plan-06-estimate-model</c>, ТЗ PLAN-1,
/// PLAN-4, PLAN-9.1, PLAN-11).
///
/// <para><b>Страницы по курсору</b>, а не по смещению: строк в редакции тысячи, а курсор — порядок
/// строки в файле — у строки неизменен. Смещение на большой редакции читало бы всё до нужной страницы.</para>
///
/// <para><b>Себестоимость</b> — стоимость единицы, итог, сметное наполнение по схеме <c>ПозицияСметы</c>,
/// цена и стоимость материала — уходит в ответ только с <c>plan.estimate.cost</c>. Без права их нет в
/// ответе вовсе — ни пустыми, ни нулём, — а есть носитель <c>hidden</c> с кодом права (ТЗ PLAN-11, PLAN-23).
/// Сметное наполнение закрыто ЦЕЛИКОМ, а не по денежным полям: поле, добавленное заказчиком без пометки,
/// утекло бы молча.</para>
///
/// <para>⚠️ Это чтение ОДНОЙ редакции для показа, а не план стройки: суммы плана — только
/// <see cref="PlanRows.Current" />.</para>
/// </summary>
public static class EstimateRowsEndpoints
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/plan/revisions/{id:guid}/rows", RowsAsync)
            .WithTags("Сметы")
            .RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateRead));

    private static async Task<IResult> RowsAsync(
        Guid id, string? cursor, int? limit, PlanDbContext db, IModuleUser user, CancellationToken ct)
    {
        var revision = await db.EstimateRevisions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Редакция не найдена — возможно, её удалили.");

        var after = 0;
        if (!string.IsNullOrEmpty(cursor) && !int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out after))
            throw new InvalidRequestException("Курсор страницы записан не так: ожидается значение поля «next» из прежнего ответа.");
        var take = limit is { } l ? Math.Clamp(l, 1, MaxLimit) : DefaultLimit;

        var rows = await db.EstimateRows.AsNoTracking()
            .Where(r => r.RevisionId == id && r.Order > after)
            .OrderBy(r => r.Order)
            .Take(take + 1)
            .ToListAsync(ct);
        var more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);

        var hidden = await EstimateViews.HiddenAsync(user, ct);
        var items = await ViewsAsync(db, revision, rows, hidden is null, ct);
        var next = more ? rows[^1].Order.ToString(CultureInfo.InvariantCulture) : null;
        return Results.Ok(new EstimateRowsPageDto(items, next, hidden));
    }

    /// <summary>
    /// Строки редакции ответом — с ресурсами и пометками. Одним местом на чтение строк и на ответ сопоставления
    /// (<c>plan-08</c>): строка, только что сопоставленная, обязана выглядеть так же, как при следующем чтении.
    /// </summary>
    internal static async Task<List<EstimateRowDto>> ViewsAsync(
        PlanDbContext db, EstimateRevision revision, IReadOnlyList<EstimateRow> rows, bool cost, CancellationToken ct)
    {
        var rowIds = rows.Select(r => r.Id).ToList();
        var materials = (await db.EstimateRowMaterials.AsNoTracking()
                .Where(m => rowIds.Contains(m.RowId)).OrderBy(m => m.Order).ToListAsync(ct))
            .ToLookup(m => m.RowId);
        var labor = (await db.EstimateRowLabor.AsNoTracking()
                .Where(x => rowIds.Contains(x.RowId)).OrderBy(x => x.Order).ToListAsync(ct))
            .ToLookup(x => x.RowId);
        var memory = await RateMarks.ForAsync(db, revision, rows, ct);
        var resources = await ResourceMarks.ForAsync(db, materials.SelectMany(g => g), ct);
        return [.. rows.Select(r => Row(r, materials[r.Id], labor[r.Id], memory.Of(r), resources, cost))];
    }

    private static EstimateRowDto Row(
        EstimateRow row, IEnumerable<EstimateRowMaterial> materials, IEnumerable<EstimateRowLabor> labor,
        IReadOnlyList<PlanMark> rateMarks, ResourceMarks resources, bool cost)
    {
        var section = row.LsrSectionCode is null && row.LsrSectionName is null
            ? null
            : new LsrSectionDto(row.LsrSectionCode, row.LsrSectionName);
        return new EstimateRowDto(
            row.Id, row.Order, section, row.NumberInSection, row.RateCode, row.Name, row.SourceVolume, row.SourceUnit,
            row.WorkTypeId, row.UnitId, row.Factor, row.Volume, row.PlanItemId, [.. Marks(row), .. rateMarks],
            [.. materials.Select(m => Material(m, resources.Of(m), cost))],
            [.. labor.Select(x => new EstimateLaborDto(x.ManHours))],
            cost ? new EstimateRowCostDto(row.UnitCost, row.Total, Data(row.Data)) : null);
    }

    private static EstimateMaterialDto Material(EstimateRowMaterial m, IReadOnlyList<PlanMark> remembered, bool cost) => new(
        m.Id, m.Order, m.NomenclatureId, m.SourceName, m.SourceCode, m.SourceUnit, m.Quantity,
        m.Unmatched ? [new("unmatched", "Не сопоставлен: нет позиции номенклатуры")] : remembered,
        cost ? new MaterialCostDto(m.UnitPrice, m.Amount) : null);

    /// <summary>«Не сопоставлена» (ТЗ PLAN-4, D17 зонтика): строка видна, но в план не входит — и сказано почему.</summary>
    private static IReadOnlyList<PlanMark> Marks(EstimateRow row)
    {
        if (!row.Unmatched) return [];
        var missing = new List<string>(2);
        if (row.WorkTypeId is null) missing.Add("вида работы");
        if (row.PlanItemId is null) missing.Add("позиции перечня");
        return [new("unmatched", $"Не сопоставлена: нет {string.Join(" и ", missing)} — в план стройки не входит")];
    }

    private static JsonElement Data(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
