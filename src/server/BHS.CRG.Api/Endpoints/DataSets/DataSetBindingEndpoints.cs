using System.Security.Claims;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules;
using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Api.Endpoints.DataSets;

public static class DataSetBindingEndpoints
{
    public static void MapDataSetBindingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/datasets/bindings").RequireAuthorization(AppPolicies.Permission(CorePermissions.DataSetsRead));

        g.MapGet("", async (Guid ownerId, ClaimsPrincipal user, IRequestDataAccessResolver access,
            IDataSetService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListBindingsAsync(ownerId, ct, await access.ForAsync(user, ct))));

        // Literal route — registered before /{id:guid} so it is matched first.
        // Предпросмотр привязок читает строки источников в правах запрашивающего (CORE-24.1).
        g.MapGet("/preview", async (Guid ownerId,
            ClaimsPrincipal user, IRequestDataAccessResolver access, IDataSetService svc, CancellationToken ct) =>
            Results.Ok(await svc.PreviewBindingsAsync(ownerId, await access.ForAsync(user, ct), ct)));

        g.MapPost("", async (CreateBindingRequest req, ClaimsPrincipal user, IRequestDataAccessResolver access,
            IDataSetService svc, CancellationToken ct) =>
        {
            var input = new CreateBindingInput(req.OwnerId, req.SourceId, req.TargetFieldKey, req.Mapping);
            try
            {
                var result = await svc.CreateBindingAsync(input, ct, await access.ForAsync(user, ct));
                return result is null ? Results.NotFound(new { error = "DataSetSource не найден" }) : Results.Ok(result);
            }
            catch (InvalidRequestException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapPut("/{id:guid}", async (Guid id, UpdateBindingRequest req, ClaimsPrincipal user,
            IRequestDataAccessResolver access, IDataSetService svc, CancellationToken ct) =>
        {
            var input = new UpdateBindingInput(req.TargetFieldKey, req.Mapping);
            var result = await svc.UpdateBindingAsync(id, input, ct, await access.ForAsync(user, ct));
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        g.MapDelete("/{id:guid}", async (Guid id, IDataSetService svc, CancellationToken ct) =>
            await svc.DeleteBindingAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }

    private record CreateBindingRequest(
        Guid OwnerId, Guid SourceId, string? TargetFieldKey, Dictionary<string, string>? Mapping);

    private record UpdateBindingRequest(string? TargetFieldKey, Dictionary<string, string>? Mapping);
}
