using System.Security.Claims;
using System.Text.Json;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Reconciliation;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Reconciliation;

public static class ReconciliationEndpoints
{
    public static void MapReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        // Определение сверки — конфигурация, как типы и шаблоны.
        var admin = app.MapGroup("/api/reconciliations").RequireAuthorization("Admin");
        // Прогон и разбор находок — работа: их ведёт тот, кто отвечает за комплект, а не администратор.
        var user = app.MapGroup("/api/reconciliations").RequireAuthorization();

        user.MapGet("/", async (string? scope, Guid? scopeId, IMediator m) =>
        {
            CatalogScope? s = scope is not null && Enum.TryParse<CatalogScope>(scope, true, out var v) ? v : null;
            var items = await m.Send(new ListReconciliationsQuery(s, scopeId));
            return Results.Ok(items.Select(ToDto));
        });

        user.MapGet("/{id:guid}", async (Guid id, IMediator m) =>
        {
            var d = await m.Send(new GetReconciliationQuery(id));
            return d is null ? Results.NotFound() : Results.Ok(ToDto(d));
        });

        admin.MapPost("/", async (CreateReq req, IMediator m) =>
        {
            var scope = Enum.TryParse<CatalogScope>(req.Scope, true, out var s) ? s : CatalogScope.System;
            var d = await m.Send(new CreateReconciliationCommand(
                req.Name, scope, req.ScopeId, JsonDocument.Parse(req.Spec.GetRawText())));
            return Results.Ok(ToDto(d));
        });

        admin.MapPut("/{id:guid}", async (Guid id, UpdateReq req, IMediator m) =>
        {
            try
            {
                var d = await m.Send(new UpdateReconciliationCommand(
                    id, req.Name, JsonDocument.Parse(req.Spec.GetRawText())));
                return Results.Ok(ToDto(d));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        admin.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            try
            {
                await m.Send(new DeleteReconciliationCommand(id));
                return Results.NoContent();
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        // ── Прогоны ─────────────────────────────────────────────────────────────

        user.MapPost("/{id:guid}/run", async (Guid id, IMediator m) =>
        {
            try { return Results.Ok(ToDto(await m.Send(new RunReconciliationCommand(id)))); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        user.MapGet("/{id:guid}/runs", async (Guid id, int? limit, IMediator m) =>
            Results.Ok((await m.Send(new ListReconciliationRunsQuery(id, limit ?? 20))).Select(ToDto)));

        user.MapGet("/{id:guid}/findings", async (Guid id, Guid? runId, IMediator m) =>
            Results.Ok((await m.Send(new ListFindingsQuery(id, runId))).Select(ToDto)));

        // ── Решения ─────────────────────────────────────────────────────────────

        user.MapPut("/{id:guid}/decisions", async (Guid id, DecisionReq req, IMediator m, ClaimsPrincipal u) =>
        {
            var kind = Enum.TryParse<DecisionKind>(req.Kind, true, out var k) ? k : DecisionKind.Accepted;
            var by = u.FindFirst("displayName")?.Value ?? u.FindFirstValue(ClaimTypes.Email);
            var d = await m.Send(new SetDecisionCommand(id, req.Key, kind, req.Note, by));
            return Results.Ok(ToDto(d));
        });

        user.MapDelete("/{id:guid}/decisions", async (Guid id, string key, IMediator m) =>
        {
            await m.Send(new RemoveDecisionCommand(id, key));
            return Results.NoContent();
        });
    }

    // ── DTO ─────────────────────────────────────────────────────────────────────

    private record CreateReq(string Name, string Scope, Guid? ScopeId, JsonElement Spec);
    private record UpdateReq(string Name, JsonElement Spec);
    /// <summary>Решение адресуется ключом позиции, а не идентификатором находки: находка живёт один
    /// прогон, решение обязано пережить любое их число.</summary>
    private record DecisionReq(string Key, string Kind, string? Note);

    private static object ToDto(ReconciliationDefinition d) => new
    {
        d.Id,
        d.Name,
        scope = d.Scope.ToString(),
        d.ScopeId,
        spec = d.Spec.RootElement,
        d.UpdatedAt,
    };

    private static object ToDto(ReconciliationRun r) => new
    {
        r.Id,
        r.DefinitionId,
        status = r.Status.ToString(),
        r.StartedAt,
        r.FinishedAt,
        r.Error,
        r.MatchCount,
        r.MismatchCount,
        r.MissingLeftCount,
        r.MissingRightCount,
    };

    private static object ToDto(FindingView v) => new
    {
        v.Finding.Id,
        v.Finding.Key,
        v.Finding.Label,
        v.Finding.LeftValue,
        v.Finding.RightValue,
        status = v.Finding.Status.ToString(),
        provenance = v.Finding.Provenance.RootElement,
        // Вычисляется из истории прогонов, не хранится (#414).
        v.Resolved,
        decision = v.Decision is null ? null : ToDto(v.Decision),
    };

    private static object ToDto(ReconciliationDecision d) => new
    {
        d.Id,
        d.Key,
        kind = d.Kind.ToString(),
        d.Note,
        d.DecidedBy,
        d.UpdatedAt,
    };
}
