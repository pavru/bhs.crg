using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Plan.Schedules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Endpoints;

internal static class ScheduleEndpoints
{
    internal const string Root = "/api/plan/schedules";

    internal static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Root).WithTags("Графики производства работ");
        var read = AppPolicies.Permission(PlanModule.ScheduleRead);
        var edit = AppPolicies.Permission(PlanModule.ScheduleEdit);
        group.MapGet("", ListAsync).RequireAuthorization(read);
        group.MapGet("/{id:guid}", ReadAsync).RequireAuthorization(read);
        group.MapPost("", CreateAsync).RequireAuthorization(edit);
        group.MapGet("/{id:guid}/rebuild-preview", PreviewAsync).RequireAuthorization(edit);
        group.MapPost("/{id:guid}/rebuild", RebuildAsync).RequireAuthorization(edit);
        group.MapPost("/{id:guid}/works", AddWorkAsync).RequireAuthorization(edit);
        group.MapPut("/{id:guid}/works/{workId:guid}", EditWorkAsync).RequireAuthorization(edit);
        group.MapDelete("/{id:guid}/works/{workId:guid}", DeleteWorkAsync).RequireAuthorization(edit);
    }

    private static async Task<IResult> ListAsync(Guid constructionId, PlanDbContext db, CancellationToken ct)
    {
        var schedules = await db.Schedules.AsNoTracking().Where(s => s.ConstructionId == constructionId)
            .OrderBy(s => s.Name).ThenBy(s => s.Id).ToListAsync(ct);
        return Results.Ok(schedules.Select(s => new ScheduleSummary(s.Id, s.ConstructionId, s.RevisionId, s.Name,
            db.VersionOf(s))).ToList());
    }

    private static async Task<IResult> CreateAsync(ScheduleCreate input, ScheduleBuilder builder, CancellationToken ct)
    {
        var (view, created) = await builder.CreateAsync(input, ct);
        return created ? Results.Created($"{Root}/{view.Id}", view) : Results.Ok(view);
    }

    private static async Task<IResult> ReadAsync(Guid id, ScheduleBuilder builder, CancellationToken ct) =>
        Results.Ok(await builder.ReadAsync(id, ct));

    private static async Task<IResult> PreviewAsync(Guid id, ScheduleBuilder builder, CancellationToken ct) =>
        Results.Ok(await builder.PreviewAsync(id, ct));

    private static async Task<IResult> RebuildAsync(
        Guid id, ScheduleRebuildRequest input, HttpRequest request, ScheduleBuilder builder, CancellationToken ct)
    {
        var (view, preview) = await builder.RebuildAsync(id, input.Fingerprint, request, ct);
        return view is not null ? Results.Ok(view) : Results.Conflict(new
        {
            error = "Распределение изменилось после предпросмотра. Сверьте новую разницу и подтвердите ещё раз.",
            preview,
        });
    }

    private static async Task<IResult> AddWorkAsync(
        Guid id, ScheduleManualWork input, ScheduleWorkEditor editor, CancellationToken ct) =>
        Results.Ok(await editor.AddAsync(id, input, ct));

    private static async Task<IResult> EditWorkAsync(
        Guid id, Guid workId, ScheduleWorkInput input, HttpRequest request, ScheduleWorkEditor editor,
        CancellationToken ct) =>
        Results.Ok(await editor.EditAsync(id, workId, input, request, ct));

    private static async Task<IResult> DeleteWorkAsync(
        Guid id, Guid workId, HttpRequest request, ScheduleWorkEditor editor, CancellationToken ct)
    {
        await editor.DeleteAsync(id, workId, request, ct);
        return Results.NoContent();
    }
}
