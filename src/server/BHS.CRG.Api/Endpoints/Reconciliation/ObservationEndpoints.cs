using System.Security.Claims;
using BHS.CRG.Application.Reconciliation;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Reconciliation;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Reconciliation;

/// <summary>
/// Замечания внешнего анализа (issue #440). Разбор — работа пользователя, ведущего комплект, поэтому
/// политика обычная; писать замечания через REST не даём: их источник — агент через MCP.
/// </summary>
public static class ObservationEndpoints
{
    public static void MapObservationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/observations").RequireAuthorization();

        g.MapGet("/", async (string? scope, Guid? scopeId, string? status, IMediator m) =>
        {
            CatalogScope? s = Enum.TryParse<CatalogScope>(scope, true, out var sv) ? sv : null;
            ObservationStatus? st = Enum.TryParse<ObservationStatus>(status, true, out var stv) ? stv : null;
            var items = await m.Send(new ListObservationsQuery(s, scopeId, st));
            return Results.Ok(items.Select(ToDto));
        });

        g.MapPut("/{id:guid}/review", async (Guid id, ReviewReq req, IMediator m, ClaimsPrincipal u) =>
        {
            if (!Enum.TryParse<ObservationStatus>(req.Status, true, out var status))
                return Results.BadRequest(new { error = $"Неизвестный статус: «{req.Status}»." });
            // Вернуть замечание в работу можно — человек вправе передумать.
            var by = u.FindFirst("displayName")?.Value ?? u.FindFirstValue(ClaimTypes.Email);
            try
            {
                return Results.Ok(ToDto(await m.Send(new ReviewObservationCommand(id, status, req.Note, by))));
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        g.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            try { await m.Send(new DeleteObservationCommand(id)); return Results.NoContent(); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
    }

    private record ReviewReq(string Status, string? Note);

    private static object ToDto(AgentObservation o) => new
    {
        o.Id,
        scope = o.Scope.ToString(),
        o.ScopeId,
        o.Key,
        o.Title,
        o.Detail,
        severity = o.Severity.ToString(),
        status = o.Status.ToString(),
        // Разворачиваем и на чтении: иначе уже записанные строкой ссылки остались бы
        // непроверяемыми (#442).
        references = ObservationReferences.Unwrap(o.References.RootElement),
        o.ReportedBy,
        o.ReviewedBy,
        o.ReviewNote,
        o.ReviewedAt,
        o.UpdatedAt,
    };
}
