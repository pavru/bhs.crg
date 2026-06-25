using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Api.Endpoints.DataSets;

public static class DataSetBindingEndpoints
{
    public static void MapDataSetBindingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/datasets/bindings").RequireAuthorization();

        g.MapGet("", async (Guid instanceId, IDataSetService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListBindingsAsync(instanceId, ct)));

        // Literal route — registered before /{id:guid} so it is matched first.
        g.MapGet("/preview", async (Guid instanceId, IDataSetService svc, CancellationToken ct) =>
            Results.Ok(await svc.PreviewBindingsAsync(instanceId, ct)));

        g.MapPost("", async (CreateBindingRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            var input = new CreateBindingInput(
                req.InstanceId, req.SourceId, req.TargetFieldKey, req.Mapping, req.RowFilter, req.ComputedColumns);
            var result = await svc.CreateBindingAsync(input, ct);
            return result is null ? Results.NotFound(new { error = "DataSetSource не найден" }) : Results.Ok(result);
        });

        g.MapPut("/{id:guid}", async (Guid id, UpdateBindingRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            var input = new UpdateBindingInput(req.TargetFieldKey, req.Mapping, req.RowFilter, req.ComputedColumns);
            var result = await svc.UpdateBindingAsync(id, input, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        g.MapDelete("/{id:guid}", async (Guid id, IDataSetService svc, CancellationToken ct) =>
            await svc.DeleteBindingAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }

    private record CreateBindingRequest(
        Guid InstanceId, Guid SourceId, string? TargetFieldKey,
        Dictionary<string, string>? Mapping, object? RowFilter, object? ComputedColumns);

    private record UpdateBindingRequest(
        string? TargetFieldKey, Dictionary<string, string>? Mapping, object? RowFilter, object? ComputedColumns);
}
