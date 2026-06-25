using System.Text.Json;
using BHS.CRG.Application.Catalog;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Catalog;

public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/catalog").RequireAuthorization();

        g.MapGet("/", async (string? entityType, IMediator m)
            => Results.Ok(await m.Send(new ListCatalogEntitiesQuery(entityType, null))));

        g.MapGet("/{id:guid}", async (Guid id, IMediator m) =>
        {
            var e = await m.Send(new GetCatalogEntityQuery(id));
            return e is null ? Results.NotFound() : Results.Ok(e);
        });

        g.MapPost("/", async (CreateEntityRequest req, IMediator m)
            => Results.Ok(await m.Send(new CreateCatalogEntityCommand(
                req.EntityType, req.DisplayName,
                JsonDocument.Parse(req.Data), req.OwnerId))));

        g.MapPut("/{id:guid}", async (Guid id, UpdateEntityRequest req, IMediator m)
            => Results.Ok(await m.Send(new UpdateCatalogEntityCommand(
                id, req.DisplayName, JsonDocument.Parse(req.Data)))));

        g.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new DeleteCatalogEntityCommand(id));
            return Results.NoContent();
        });
    }

    record CreateEntityRequest(string EntityType, string DisplayName, string Data, Guid? OwnerId);
    record UpdateEntityRequest(string DisplayName, string Data);
}
