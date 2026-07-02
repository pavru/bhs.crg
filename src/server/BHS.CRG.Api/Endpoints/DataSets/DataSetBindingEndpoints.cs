using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Api.Endpoints.DataSets;

public static class DataSetBindingEndpoints
{
    public static void MapDataSetBindingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/datasets/bindings").RequireAuthorization();

        g.MapGet("", async (Guid? instanceId, Guid? commonDataEntryId, IDataSetService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListBindingsAsync(instanceId, commonDataEntryId, ct)));

        // Literal route — registered before /{id:guid} so it is matched first.
        g.MapGet("/preview", async (Guid? instanceId, Guid? commonDataEntryId, IDataSetService svc, CancellationToken ct) =>
            Results.Ok(await svc.PreviewBindingsAsync(instanceId, commonDataEntryId, ct)));

        g.MapPost("", async (CreateBindingRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            var input = new CreateBindingInput(req.InstanceId, req.CommonDataEntryId, req.SourceId, req.TargetFieldKey, req.Mapping);
            try
            {
                var result = await svc.CreateBindingAsync(input, ct);
                return result is null ? Results.NotFound(new { error = "DataSetSource не найден" }) : Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        g.MapPut("/{id:guid}", async (Guid id, UpdateBindingRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            var input = new UpdateBindingInput(req.TargetFieldKey, req.Mapping);
            var result = await svc.UpdateBindingAsync(id, input, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        g.MapDelete("/{id:guid}", async (Guid id, IDataSetService svc, CancellationToken ct) =>
            await svc.DeleteBindingAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }

    private record CreateBindingRequest(
        Guid? InstanceId, Guid? CommonDataEntryId, Guid SourceId, string? TargetFieldKey, Dictionary<string, string>? Mapping);

    private record UpdateBindingRequest(string? TargetFieldKey, Dictionary<string, string>? Mapping);
}
