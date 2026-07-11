using System.Text.Json;
using BHS.CRG.Application.Catalog;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Catalog;

public static class EnumTypeEndpoints
{
    public static void MapEnumTypeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/enum-types").RequireAuthorization();
        var admin = app.MapGroup("/api/enum-types").RequireAuthorization("Admin");

        g.MapGet("/", async (IMediator m) =>
            Results.Ok(await m.Send(new ListEnumTypesQuery())));

        admin.MapPost("/", async (EnumTypeRequest req, IMediator m) =>
            Results.Ok(await m.Send(new CreateEnumTypeCommand(
                req.Name, req.Code, req.Description, JsonDocument.Parse(req.Values)))));

        admin.MapPut("/{id:guid}", async (Guid id, EnumTypeRequest req, IMediator m) =>
            Results.Ok(await m.Send(new UpdateEnumTypeCommand(
                id, req.Name, req.Code, req.Description, JsonDocument.Parse(req.Values)))));

        admin.MapPut("/{id:guid}/group", async (Guid id, SetGroupRequest req, IMediator m)
            => Results.Ok(await m.Send(new SetEnumTypeGroupCommand(id, req.Group))));

        admin.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            try { await m.Send(new DeleteEnumTypeCommand(id)); return Results.NoContent(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
    }

    record EnumTypeRequest(string Name, string Code, string? Description, string Values);
    record SetGroupRequest(string? Group);
}
