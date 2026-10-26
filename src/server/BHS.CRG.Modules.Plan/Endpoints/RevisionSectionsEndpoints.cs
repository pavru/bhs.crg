using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Разделы ЛСР редакции со счётчиками (change <c>plan-31-estimate-ui-carriers</c>, D5, ТЗ PLAN-6.1): таблица
/// смет рисует свёрнутые разделы, не читая строк, — одним запросом, в порядке файла.
///
/// <para>Счётчики — те же правила, что у редакции (<see cref="EstimateViews" />): «не сопоставлена» — только
/// строка-работа, материалы без номенклатуры — у строк всех видов. Единицы расценки — у строк-работ: по ним
/// сопоставляют раздел, а строку-материал раздел пропускает.</para>
///
/// <para>⚠️ Чтение ОДНОЙ редакции для показа, а не план стройки — суммы плана только <see cref="PlanRows.Current" />.</para>
/// </summary>
public static class RevisionSectionsEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/plan/revisions/{id:guid}/sections", SectionsAsync)
            .WithTags("Сметы")
            .RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateRead));

    private static async Task<IResult> SectionsAsync(Guid id, PlanDbContext db, CancellationToken ct)
    {
        if (!await db.EstimateRevisions.AnyAsync(r => r.Id == id, ct))
            throw new NotFoundException("Редакция не найдена — возможно, её удалили.");

        var rows = db.EstimateRows.AsNoTracking().Where(r => r.RevisionId == id);
        var sections = await rows
            .GroupBy(r => new { r.LsrSectionCode, r.LsrSectionName })
            .Select(g => new
            {
                g.Key.LsrSectionCode,
                g.Key.LsrSectionName,
                First = g.Min(r => r.Order),
                Rows = g.Count(),
                Unmatched = g.Count(r => r.Kind == EstimateRowKind.Work && (r.WorkTypeId == null || r.PlanItemId == null)),
            })
            .ToListAsync(ct);
        var materials = await (
                from m in db.EstimateRowMaterials.AsNoTracking()
                join r in rows on m.RowId equals r.Id
                where m.NomenclatureId == null
                group m by new { r.LsrSectionCode, r.LsrSectionName } into g
                select new { g.Key.LsrSectionCode, g.Key.LsrSectionName, Count = g.Count() })
            .ToListAsync(ct);
        var units = await rows
            .Where(r => r.Kind == EstimateRowKind.Work)
            .GroupBy(r => new { r.LsrSectionCode, r.LsrSectionName, r.SourceUnit })
            .Select(g => new { g.Key.LsrSectionCode, g.Key.LsrSectionName, g.Key.SourceUnit, First = g.Min(r => r.Order), Rows = g.Count() })
            .ToListAsync(ct);

        return Results.Ok(new RevisionSectionsDto([
            .. sections.OrderBy(s => s.First).Select(s => new RevisionSectionDto(
                s.LsrSectionCode, s.LsrSectionName, s.Rows, s.Unmatched,
                materials.FirstOrDefault(m => m.LsrSectionCode == s.LsrSectionCode && m.LsrSectionName == s.LsrSectionName)?.Count ?? 0,
                [.. units.Where(u => u.LsrSectionCode == s.LsrSectionCode && u.LsrSectionName == s.LsrSectionName)
                    .OrderBy(u => u.First).Select(u => new SectionUnitDto(u.SourceUnit, u.Rows))])),
        ]));
    }
}

/// <param name="SourceUnit">Единица расценки, как в файле.</param>
/// <param name="Rows">Строк-работ раздела в этой единице.</param>
public sealed record SectionUnitDto(string? SourceUnit, int Rows);

/// <param name="Code">Код раздела ЛСР — то, что называет <c>sections/match</c> и <c>lines/match</c>.</param>
/// <param name="Rows">Строк раздела — всех видов.</param>
/// <param name="Unmatched">Строк-работ без вида работы или позиции перечня.</param>
/// <param name="MaterialsUnmatched">Материалов без позиции номенклатуры.</param>
/// <param name="Units">Единицы расценки строк-работ, в порядке файла.</param>
public sealed record RevisionSectionDto(
    string? Code, string? Name, int Rows, int Unmatched, int MaterialsUnmatched, IReadOnlyList<SectionUnitDto> Units);

public sealed record RevisionSectionsDto(IReadOnlyList<RevisionSectionDto> Items);
