using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;
using System.Text.Json;
using BHS.CRG.Application.Catalog;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Catalog;

public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        // Чтение и запись — РАЗНЫМИ правами. Закрыть группу целиком одним правом на чтение значило
        // бы, что всякий, кто видит справочник, может его и править: право на правку осталось бы
        // объявленным и не стоящим ни на одной двери. Поймано ратчетом NotYetUsed (issue #947).
        var g = app.MapGroup("/api/catalog").RequireAuthorization(AppPolicies.Permission(CorePermissions.CatalogRead));
        var edit = app.MapGroup("/api/catalog").RequireAuthorization(AppPolicies.Permission(CorePermissions.CatalogEdit));

        g.MapGet("/", async (string? entityType, IMediator m)
            => Results.Ok(await m.Send(new ListCatalogEntitiesQuery(entityType, null))));

        g.MapGet("/{id:guid}", async (Guid id, IMediator m) =>
        {
            var e = await m.Send(new GetCatalogEntityQuery(id));
            return e is null ? Results.NotFound() : Results.Ok(e);
        });

        edit.MapPost("/", async (CreateEntityRequest req, IMediator m)
            => Results.Ok(await m.Send(new CreateCatalogEntityCommand(
                req.EntityType, req.DisplayName,
                JsonDocument.Parse(req.Data), req.OwnerId))));

        edit.MapPut("/{id:guid}", async (Guid id, UpdateEntityRequest req, IMediator m)
            => Results.Ok(await m.Send(new UpdateCatalogEntityCommand(
                id, req.DisplayName, JsonDocument.Parse(req.Data)))));

        edit.MapDelete("/{id:guid}", async (Guid id, IMediator m) =>
        {
            await m.Send(new DeleteCatalogEntityCommand(id));
            return Results.NoContent();
        });
    }

    record CreateEntityRequest(string EntityType, string DisplayName, string Data, Guid? OwnerId);
    record UpdateEntityRequest(string DisplayName, string Data);
}
