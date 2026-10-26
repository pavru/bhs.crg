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
/// Сопоставление строк редакции с видами работ (change <c>plan-08-matching</c>, ТЗ PLAN-6.1, PLAN-5, AUTH-8,
/// AUTH-9): строку — <c>PUT …/lines/{lineId}/match</c>, раздел ЛСР — <c>POST …/sections/match</c>, выбранные
/// строки или несколько разделов — <c>POST …/lines/match</c> (D9 <c>plan-31</c>). Коэффициент до записи —
/// <c>GET /api/plan/unit-factor</c> (D8 <c>plan-31</c>): тем же правилом, ничего не записывая.
///
/// <para><b>Ворота и версия.</b> Группа модуля закрыта воротами <c>module:plan</c>, адреса — правом
/// <c>plan.estimate.edit</c>. Версию называет РЕДАКЦИЯ заголовком <c>If-Match</c>: без него — 400, с устаревшей
/// — 409. Сопоставление сдвигает версию редакции, и две вкладки, сопоставляющие одну смету, узнают друг о
/// друге отказом, а не молча поверх.</para>
///
/// <para><b>Снять сопоставление</b> — тем же адресом строки с <c>clear: true</c>: пометку «запомнено» так и
/// отменяют одним действием. Пустое тело ничего не снимает — запрос без вида работы и без <c>clear</c> отвергнут,
/// иначе забытое поле стирало бы сопоставление.</para>
/// </summary>
public static class MatchEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var edit = AppPolicies.Permission(PlanModule.EstimateEdit);
        endpoints.MapPut("/api/plan/revisions/{id:guid}/lines/{lineId:guid}/match", MatchRowAsync)
            .WithTags("Сметы").RequireAuthorization(edit);
        endpoints.MapPost("/api/plan/revisions/{id:guid}/sections/match", MatchSectionAsync)
            .WithTags("Сметы").RequireAuthorization(edit);
        endpoints.MapPost("/api/plan/revisions/{id:guid}/lines/match", MatchLinesAsync)
            .WithTags("Сметы").RequireAuthorization(edit);
        endpoints.MapGet("/api/plan/unit-factor", UnitFactorAsync)
            .WithTags("Сметы").RequireAuthorization(AppPolicies.Permission(PlanModule.EstimateRead));
    }

    private static async Task<IResult> MatchRowAsync(
        Guid id, Guid lineId, RowMatchRequest body, HttpRequest request, PlanDbContext db, RowMatching matching,
        IModuleUser user, CancellationToken ct)
    {
        var revision = await SeenAsync(db, id, request, ct);
        var row = await db.EstimateRows.FirstOrDefaultAsync(r => r.Id == lineId && r.RevisionId == id, ct)
            ?? throw new NotFoundException("Строки нет в этой редакции — перечитайте строки редакции.");

        RateChoice? choice = body.Clear == true
            ? null
            : new(body.WorkTypeId ?? throw new InvalidRequestException(
                "Вид работы не назван («workTypeId»). Чтобы снять сопоставление строки, пришлите «clear»: true."),
                body.UnitId, body.Factor);
        var outcome = await matching.MatchRowAsync(revision, row, choice, body.Remember ?? true, ct);

        var hidden = await EstimateViews.HiddenAsync(user, ct);
        var view = (await EstimateRowsEndpoints.ViewsAsync(db, revision, [row], hidden is null, ct))[0];
        return Results.Ok(new RowMatchDto(view, db.VersionOf(revision), outcome.Units, outcome.Memory, hidden));
    }

    private static async Task<IResult> MatchSectionAsync(
        Guid id, SectionMatchRequest body, HttpRequest request, PlanDbContext db, RowMatching matching, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.LsrSectionCode))
            throw new InvalidRequestException("Раздел ЛСР не назван («lsrSectionCode») — поле «lsrSection.code» строки.");
        if (body.WorkTypeId is not { } workType)
            throw new InvalidRequestException("Вид работы не назван («workTypeId»).");
        var revision = await SeenAsync(db, id, request, ct);

        var outcome = await matching.MatchSectionAsync(revision, body.LsrSectionCode,
            new RateChoice(workType, body.UnitId, body.Factor), body.SourceUnit, body.Remember ?? true, ct);
        return Results.Ok(new SectionMatchDto(
            outcome.Matched, outcome.Skipped, db.VersionOf(revision), outcome.Units, outcome.Memory));
    }

    private static async Task<IResult> MatchLinesAsync(
        Guid id, LinesMatchRequest body, HttpRequest request, PlanDbContext db, RowMatching matching, CancellationToken ct)
    {
        if (body.WorkTypeId is not { } workType)
            throw new InvalidRequestException("Вид работы не назван («workTypeId»).");
        var revision = await SeenAsync(db, id, request, ct);

        var outcome = await matching.MatchLinesAsync(revision, body.LineIds, body.Sections,
            new RateChoice(workType, body.UnitId, body.Factor), body.SourceUnit, body.Remember ?? true, ct);
        return Results.Ok(new SectionMatchDto(
            outcome.Matched, outcome.Skipped, db.VersionOf(revision), outcome.Units, outcome.Memory));
    }

    /// <summary>
    /// Коэффициент из единицы расценки в единицу классификатора — предложение сервера тем же правилом, что у
    /// сопоставления (<see cref="RateMatching.SuggestFactor" />), без записи (D8 <c>plan-31</c>). Не выводится —
    /// <c>factor: null</c> с причиной, а не 1: единица вместо неизвестного дала бы план во сто раз меньше.
    /// </summary>
    private static async Task<IResult> UnitFactorAsync(
        string? sourceUnit, Guid? unitId, IModuleUnits units, CancellationToken ct)
    {
        if (unitId is not { } wanted)
            throw new InvalidRequestException("Единица классификатора не названа («unitId»).");
        var unit = (await units.GetAsync([wanted], ct) ?? throw new InvalidRequestException(IModuleUnits.NotSetUp))
            .FirstOrDefault(u => u.Id == wanted)
            ?? throw new InvalidRequestException("Такой единицы измерения нет — её удалили либо это запись другого вида.");
        var factor = RateMatching.SuggestFactor(sourceUnit, unit.Name);
        return Results.Ok(new UnitFactorDto(factor, sourceUnit, unit.Id, unit.Name, factor is null
            ? $"Единицы не подсказывают коэффициент: расценка в «{(string.IsNullOrWhiteSpace(sourceUnit) ? "без единицы" : sourceUnit)}», " +
              $"единица классификатора — «{unit.Name}». Назовите его сами: сколько единиц классификатора в одной единице расценки."
            : null));
    }

    private static async Task<EstimateRevision> SeenAsync(PlanDbContext db, Guid id, HttpRequest request, CancellationToken ct)
    {
        var revision = await db.EstimateRevisions.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new NotFoundException("Редакция не найдена — возможно, её удалили.");
        EstimateEndpoints.EnsureSeen(request, db.VersionOf(revision), "Редакцию", "редакцию");
        return revision;
    }
}

/// <param name="Clear"><c>true</c> — снять сопоставление строки; остальные поля тогда не читаются.</param>
/// <param name="Remember">Запомнить выбор по шифру; не названо — запомнить.</param>
public sealed record RowMatchRequest(Guid? WorkTypeId, Guid? UnitId, decimal? Factor, bool? Remember, bool? Clear);

/// <param name="SourceUnit">Единица расценки, к которой назван коэффициент; не названа — единица первой
/// несопоставленной строки раздела.</param>
public sealed record SectionMatchRequest(
    string? LsrSectionCode, Guid? WorkTypeId, Guid? UnitId, decimal? Factor, string? SourceUnit, bool? Remember);

/// <summary>Выбранные строки или разделы ЛСР одним видом работы (D9 <c>plan-31</c>): ровно одно из двух.</summary>
/// <param name="SourceUnit">Единица расценки, к которой назван коэффициент; не названа — единица первой
/// несопоставленной строки, как у раздела.</param>
/// <param name="Remember">Запомнить выбор по шифрам; не названо — запомнить.</param>
public sealed record LinesMatchRequest(
    IReadOnlyList<Guid>? LineIds, IReadOnlyList<string>? Sections, Guid? WorkTypeId, Guid? UnitId, decimal? Factor,
    string? SourceUnit, bool? Remember);

/// <param name="Factor">Сколько единиц классификатора в одной единице расценки; <c>null</c> — не выводится.</param>
/// <param name="Reason">Почему коэффициента нет; <c>null</c> — он есть.</param>
public sealed record UnitFactorDto(decimal? Factor, string? SourceUnit, Guid UnitId, string Unit, string? Reason);

/// <param name="Version">Новая версия редакции — её называет следующая правка.</param>
public sealed record RowMatchDto(
    EstimateRowDto Row, string Version, MatchUnits Units, MatchMemory Memory,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HiddenPart>? Hidden);

public sealed record SectionMatchDto(
    int Matched, IReadOnlyList<SkippedRow> Skipped, string Version, MatchUnits Units, MatchMemory Memory);
