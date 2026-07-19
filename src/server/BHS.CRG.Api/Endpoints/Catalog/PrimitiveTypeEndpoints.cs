using System.Text.Json;
using BHS.CRG.Application.Catalog;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Catalog;

public static class PrimitiveTypeEndpoints
{
    public static void MapPrimitiveTypeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/primitive-types").RequireAuthorization();
        var admin = app.MapGroup("/api/primitive-types").RequireAuthorization("Admin");

        g.MapGet("/", async (IMediator m) =>
            Results.Ok(await m.Send(new ListPrimitiveTypesQuery())));

        admin.MapPost("/", async (PrimitiveTypeRequest req, IMediator m) =>
            Results.Ok(await m.Send(new CreatePrimitiveTypeCommand(
                req.Name, req.Code, req.BaseType, req.Description,
                JsonDocument.Parse(req.Constraints), req.AllowedTags))));

        admin.MapPut("/{id:guid}", async (Guid id, PrimitiveTypeRequest req, IMediator m) =>
            Results.Ok(await m.Send(new UpdatePrimitiveTypeCommand(
                id, req.Name, req.Code, req.Description,
                JsonDocument.Parse(req.Constraints), req.AllowedTags))));

        admin.MapPut("/{id:guid}/group", async (Guid id, SetGroupRequest req, IMediator m)
            => Results.Ok(await m.Send(new SetPrimitiveTypeGroupCommand(id, req.Group))));

        admin.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            try { await m.Send(new DeletePrimitiveTypeCommand(id)); return Results.NoContent(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }

    record PrimitiveTypeRequest(string Name, string Code, string BaseType, string? Description, string Constraints, string[]? AllowedTags);
    record SetGroupRequest(string? Group);
}
