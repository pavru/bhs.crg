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
/// AUTH-9): строку — <c>PUT …/lines/{lineId}/match</c>, раздел ЛСР — <c>POST …/sections/match</c>.
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

/// <param name="Version">Новая версия редакции — её называет следующая правка.</param>
public sealed record RowMatchDto(
    EstimateRowDto Row, string Version, MatchUnits Units, RateMatchMemory Memory,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HiddenPart>? Hidden);

public sealed record SectionMatchDto(
    int Matched, IReadOnlyList<SkippedRow> Skipped, string Version, MatchUnits Units, RateMatchMemory Memory);
