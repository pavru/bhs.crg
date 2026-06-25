using System.Text.Json;
using BHS.CRG.Application.Catalog;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Catalog;

public static class PrimitiveTypeEndpoints
{
    public static void MapPrimitiveTypeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/primitive-types").RequireAuthorization();

        g.MapGet("/", async (IMediator m) =>
            Results.Ok(await m.Send(new ListPrimitiveTypesQuery())));

        g.MapPost("/", async (PrimitiveTypeRequest req, IMediator m) =>
            Results.Ok(await m.Send(new CreatePrimitiveTypeCommand(
                req.Name, req.Code, req.BaseType, req.Description,
                JsonDocument.Parse(req.Constraints)))));

        g.MapPut("/{id:guid}", async (Guid id, PrimitiveTypeRequest req, IMediator m) =>
            Results.Ok(await m.Send(new UpdatePrimitiveTypeCommand(
                id, req.Name, req.Code, req.Description,
                JsonDocument.Parse(req.Constraints)))));

        g.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new DeletePrimitiveTypeCommand(id));
            return Results.NoContent();
        });
    }

    record PrimitiveTypeRequest(string Name, string Code, string BaseType, string? Description, string Constraints);
}
