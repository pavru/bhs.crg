using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
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
/// Материалы редакции по материалу (change <c>plan-31-estimate-ui-carriers</c>, D10, ТЗ PLAN-9.1): один материал
/// файла стоит в десятках строк, и сопоставлять его ресурсы по одному — десятки одинаковых решений. Группа —
/// «шифр + наименование + единица» как в файле; <c>GET …/materials</c> читает группы, <c>PUT …/materials/match</c>
/// сопоставляет все ресурсы группы одной фиксацией.
///
/// <para><b>Ворота и версия</b> — как у материала строки (<see cref="ResourceMatchEndpoints" />): чтение — правом
/// <c>plan.estimate.read</c>, сопоставление — <c>plan.estimate.edit</c> с версией редакции в <c>If-Match</c>.
/// Цена и стоимость — только с <c>plan.estimate.cost</c>.</para>
///
/// <para>⚠️ Чтение ОДНОЙ редакции для показа, а не план стройки — суммы плана только <see cref="PlanRows.Current" />.</para>
/// </summary>
public static class RevisionMaterialsEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/plan/revisions/{id:guid}/materials", ListAsync)
            .WithTags("Сметы").RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateRead));
        endpoints.MapPut("/api/plan/revisions/{id:guid}/materials/match", MatchAsync)
            .WithTags("Сметы").RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateEdit));
    }

    private static async Task<IResult> ListAsync(Guid id, PlanDbContext db, IModuleUser user, CancellationToken ct)
    {
        if (!await db.EstimateRevisions.AnyAsync(r => r.Id == id, ct))
            throw new NotFoundException("Редакция не найдена — возможно, её удалили.");
        var hidden = await EstimateViews.HiddenAsync(user, ct);
        var groups = await GroupsAsync(db, id, tracked: false, ct);
        return Results.Ok(new RevisionMaterialsDto([.. groups.Select(g => View(g, hidden is null))], hidden));
    }

    private static async Task<IResult> MatchAsync(
        Guid id, MaterialGroupMatchRequest body, HttpRequest request, PlanDbContext db, MaterialMatching matching,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Key))
            throw new InvalidRequestException("Группа не названа («key») — поле «key» из чтения материалов редакции.");
        if (body.NomenclatureId is not { } nomenclature)
            throw new InvalidRequestException("Позиция номенклатуры не названа («nomenclatureId»).");
        var revision = await db.EstimateRevisions.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Редакция не найдена — возможно, её удалили.");
        EstimateEndpoints.EnsureSeen(request, db.VersionOf(revision), "Редакцию", "редакцию");

        var group = (await GroupsAsync(db, id, tracked: true, ct)).FirstOrDefault(g => g.Key == body.Key)
            ?? throw new NotFoundException("Такого материала в редакции нет — перечитайте материалы редакции.");
        var (matched, memory) = await matching.MatchGroupAsync(revision, group.Materials, nomenclature, body.Remember ?? true, ct);
        return Results.Ok(new MaterialGroupMatchDto(group.Materials.Count, matched, db.VersionOf(revision), memory));
    }

    /// <summary>Материал файла и его ресурсы в строках редакции — в порядке первого появления.</summary>
    private sealed record Group(string Key, int Rows, IReadOnlyList<EstimateRowMaterial> Materials);

    private static async Task<List<Group>> GroupsAsync(PlanDbContext db, Guid revisionId, bool tracked, CancellationToken ct)
    {
        // Отслеживание — на весь запрос: AsNoTracking у любого источника сделал бы неотслеживаемыми и ресурсы, и
        // сопоставление группы молча не записалось бы.
        var query = from m in db.EstimateRowMaterials
                    join r in db.EstimateRows on m.RowId equals r.Id
                    where r.RevisionId == revisionId
                    orderby r.Order, m.Order
                    select new { Material = m, r.Id };
        var all = await (tracked ? query.AsTracking() : query.AsNoTracking()).ToListAsync(ct);
        return
        [
            .. all.GroupBy(x => KeyOf(x.Material))
                .Select(g => new Group(g.Key, g.Select(x => x.Id).Distinct().Count(), [.. g.Select(x => x.Material)])),
        ];
    }

    /// <summary>
    /// Ключ группы — отпечаток «шифр + наименование + единица» как в файле: без приведения регистра и пробелов,
    /// иначе один ключ склеил бы материалы, которые файл различает. Запомненное приводит текст само
    /// (<see cref="ResourceMatchKey" />) — это разные вопросы.
    /// </summary>
    private static string KeyOf(EstimateRowMaterial m) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{m.SourceCode}\u001f{m.SourceName}\u001f{m.SourceUnit}")))[..32]
            .ToLowerInvariant();

    private static MaterialGroupDto View(Group g, bool cost)
    {
        var first = g.Materials[0];
        var positions = g.Materials.Select(m => m.NomenclatureId).Distinct().ToList();
        var unmatched = g.Materials.Count(m => m.Unmatched);
        var marks = new List<PlanMark>(2);
        if (unmatched > 0)
            marks.Add(new("unmatched", unmatched == g.Materials.Count
                ? "Не сопоставлен: нет позиции номенклатуры"
                : $"Не сопоставлено ресурсов: {unmatched} из {g.Materials.Count}"));
        if (positions.OfType<Guid>().Count() > 1)
            marks.Add(new("mixed", "Ресурсы материала сопоставлены с разными позициями номенклатуры"));

        var prices = g.Materials.Select(m => m.UnitPrice).Distinct().ToList();
        return new MaterialGroupDto(
            g.Key, first.SourceName, first.SourceCode, first.SourceUnit, g.Rows, g.Materials.Sum(m => m.Quantity),
            positions is [{ } only] ? only : null, marks,
            cost
                ? new MaterialCostDto(prices is [{ } price] ? price : null,
                    g.Materials.All(m => m.Amount is not null) ? g.Materials.Sum(m => m.Amount!.Value) : null)
                : null);
    }
}

/// <param name="Remember">Запомнить выбор по коду ресурса (без кода — по наименованию); не названо — запомнить.</param>
public sealed record MaterialGroupMatchRequest(string? Key, Guid? NomenclatureId, bool? Remember);

/// <param name="Key">Ключ группы — его называет <c>PUT …/materials/match</c>.</param>
/// <param name="Rows">Строк редакции, где стоит материал.</param>
/// <param name="Quantity">Количество по всем строкам — в единице файла.</param>
/// <param name="NomenclatureId">Позиция номенклатуры, если у всех ресурсов группы она одна.</param>
/// <param name="Marks"><c>unmatched</c> — есть ресурсы без позиции; <c>mixed</c> — позиции у ресурсов разные.</param>
/// <param name="Cost">Цена единицы (если у всех одна) и стоимость — только с <c>plan.estimate.cost</c>.</param>
public sealed record MaterialGroupDto(
    string Key, string SourceName, string? SourceCode, string? SourceUnit, int Rows, decimal Quantity,
    Guid? NomenclatureId, IReadOnlyList<PlanMark> Marks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MaterialCostDto? Cost);

public sealed record RevisionMaterialsDto(
    IReadOnlyList<MaterialGroupDto> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HiddenPart>? Hidden);

/// <param name="Resources">Ресурсов в группе.</param>
/// <param name="Matched">Из них получили позицию этим запросом; остальные уже стояли на ней.</param>
/// <param name="Version">Новая версия редакции — её называет следующая правка.</param>
public sealed record MaterialGroupMatchDto(int Resources, int Matched, string Version, MatchMemory Memory);
