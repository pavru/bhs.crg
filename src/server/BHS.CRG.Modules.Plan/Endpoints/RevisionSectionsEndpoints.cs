using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Distribution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Endpoints;

/// <summary>
/// Разделы ЛСР редакции со счётчиками (change <c>plan-31-estimate-ui-carriers</c>, D5, ТЗ PLAN-6.1): таблица
/// смет рисует свёрнутые разделы, не читая строк, — одним запросом, в порядке файла.
///
/// <para>Счётчики — те же правила, что у редакции (<see cref="EstimateRowCounts" />): «не сопоставлена» — только
/// строка-работа, материалы без номенклатуры — у строк всех видов. Единицы расценки — у строк-работ: по ним
/// сопоставляют раздел, а строку-материал раздел пропускает.</para>
///
/// <para><b>Раздел — код</b>, как его называют <c>sections/match</c> и <c>lines/match</c>: счётчик обязан
/// говорить о тех строках, которые сопоставит сопоставление раздела. Два наименования при одном коде — один
/// раздел с наименованием первой строки. Строки вне разделов — один пункт с кодом <c>null</c>: разделом его не
/// сопоставить, только строками (<c>lineIds</c>).</para>
///
/// <para><b>Распределение</b> (<c>plan-10</c>): правило раздела, его исключения и нераспределённые строки — заголовок
/// раздела в режиме «Распределение» рисуется без второго запроса. Правило то же, что у ответа распределения
/// (<see cref="DistributionLines" />). У сметы на разделе стройки правил нет и нераспределённых строк нет.</para>
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
        var estimateSection = await db.EstimateRevisions.Where(r => r.Id == id)
            .Join(db.Estimates, r => r.EstimateId, e => e.Id, (r, e) => new { e.SectionId })
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Редакция не найдена — возможно, её удалили.");

        var rows = db.EstimateRows.AsNoTracking().Where(r => r.RevisionId == id);
        var sections = await EstimateRowCounts.By(rows, r => r.LsrSectionCode, total: false).ToListAsync(ct);
        var names = (await rows
                .GroupBy(r => new { r.LsrSectionCode, r.LsrSectionName })
                .Select(g => new { g.Key.LsrSectionCode, g.Key.LsrSectionName, First = g.Min(r => r.Order) })
                .ToListAsync(ct))
            .ToLookup(n => n.LsrSectionCode);
        var materials = (await (
                from m in db.EstimateRowMaterials.AsNoTracking()
                join r in rows on m.RowId equals r.Id
                where m.NomenclatureId == null
                group m by r.LsrSectionCode into g
                select new { Code = g.Key, Count = g.Count() })
            .ToListAsync(ct))
            .ToLookup(m => (string?)m.Code, m => m.Count);
        var units = (await rows
                .Where(r => r.Kind == EstimateRowKind.Work)
                .GroupBy(r => new { r.LsrSectionCode, r.SourceUnit })
                .Select(g => new { g.Key.LsrSectionCode, g.Key.SourceUnit, First = g.Min(r => r.Order), Rows = g.Count() })
                .ToListAsync(ct))
            .ToLookup(u => u.LsrSectionCode);
        var distribution = estimateSection.SectionId is null ? await DistributionAsync(db, id, ct) : null;

        // Поиск — по ToLookup: в отличие от словаря он принимает и код null, строки вне разделов.
        return Results.Ok(new RevisionSectionsDto([
            .. sections.OrderBy(s => s.First).Select(s =>
            {
                var state = distribution?.GetValueOrDefault(new LsrKey(s.Key));
                return new RevisionSectionDto(
                    s.Key, names[s.Key].MinBy(n => n.First)?.LsrSectionName, s.Rows, s.Unmatched, materials[s.Key].Sum(),
                    [.. units[s.Key].OrderBy(u => u.First).Select(u => new SectionUnitDto(u.SourceUnit, u.Rows))],
                    DistributionReader.TargetOf(state?.Rule), state?.Undistributed ?? 0, state?.Exceptions ?? 0);
            }),
        ]));
    }

    private static async Task<IReadOnlyDictionary<LsrKey, SectionState>> DistributionAsync(
        PlanDbContext db, Guid id, CancellationToken ct)
    {
        var lines = await db.EstimateRows.AsNoTracking().Where(r => r.RevisionId == id)
            .Select(r => new LineInput(r.Id, r.LsrSectionCode, r.SourceVolume)).ToListAsync(ct);
        var rules = await db.DistributionRules.AsNoTracking().Where(r => r.RevisionId == id).ToListAsync(ct);
        var parts = await db.DistributionParts.AsNoTracking().Where(p => p.RevisionId == id).ToListAsync(ct);
        return DistributionLines.Evaluate(lines, rules, parts).Sections;
    }
}

/// <param name="SourceUnit">Единица расценки, как в файле.</param>
/// <param name="Rows">Строк-работ раздела в этой единице.</param>
public sealed record SectionUnitDto(string? SourceUnit, int Rows);

/// <param name="Code">Код раздела ЛСР — то, что называет <c>sections/match</c> и <c>lines/match</c>; <c>null</c> —
/// строки вне разделов, их сопоставляют строками.</param>
/// <param name="Name">Наименование раздела — первой его строки.</param>
/// <param name="Rows">Строк раздела — всех видов.</param>
/// <param name="Unmatched">Строк-работ без вида работы или позиции перечня.</param>
/// <param name="MaterialsUnmatched">Материалов без позиции номенклатуры.</param>
/// <param name="Units">Единицы расценки строк-работ, в порядке файла.</param>
/// <param name="Rule">Правило распределения раздела (<c>plan-10</c>); <c>null</c> — правила нет.</param>
/// <param name="Undistributed">Строк раздела без распределения: ни частей, ни правила.</param>
/// <param name="Exceptions">Строк раздела со своими частями — правило к ним не применяется.</param>
public sealed record RevisionSectionDto(
    string? Code, string? Name, int Rows, int Unmatched, int MaterialsUnmatched, IReadOnlyList<SectionUnitDto> Units,
    DistributionTargetDto? Rule, int Undistributed, int Exceptions);

public sealed record RevisionSectionsDto(IReadOnlyList<RevisionSectionDto> Items);
