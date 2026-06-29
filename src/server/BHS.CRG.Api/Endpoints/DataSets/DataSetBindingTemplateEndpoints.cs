using BHS.CRG.Application.DataSets;

namespace BHS.CRG.Api.Endpoints.DataSets;

public static class DataSetBindingTemplateEndpoints
{
    public static void MapDataSetBindingTemplateEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/document-types/{docTypeId:guid}/binding-templates")
                   .RequireAuthorization();
        var admin = app.MapGroup("/api/document-types/{docTypeId:guid}/binding-templates")
                   .RequireAuthorization("Admin");

        g.MapGet("", async (Guid docTypeId, IDataSetService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListTemplatesAsync(docTypeId, ct)));

        admin.MapPost("", async (Guid docTypeId, CreateTemplateRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            var input = new CreateTemplateInput(
                req.Name, req.TargetFieldKey, req.ColumnMappings, req.RowFilter, req.ComputedColumns);
            return Results.Ok(await svc.CreateTemplateAsync(docTypeId, input, ct));
        });

        admin.MapPut("/{id:guid}", async (
            Guid docTypeId, Guid id, UpdateTemplateRequest req, IDataSetService svc, CancellationToken ct) =>
        {
            var input = new UpdateTemplateInput(
                req.Name, req.TargetFieldKey, req.ColumnMappings, req.RowFilter, req.ComputedColumns, req.SortOrder);
            var result = await svc.UpdateTemplateAsync(docTypeId, id, input, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        admin.MapDelete("/{id:guid}", async (Guid docTypeId, Guid id, IDataSetService svc, CancellationToken ct) =>
            await svc.DeleteTemplateAsync(docTypeId, id, ct) ? Results.NoContent() : Results.NotFound());
    }

    private record CreateTemplateRequest(
        string Name, string? TargetFieldKey, Dictionary<string, string>? ColumnMappings,
        object? RowFilter, object? ComputedColumns);

    private record UpdateTemplateRequest(
        string Name, string? TargetFieldKey, Dictionary<string, string>? ColumnMappings,
        object? RowFilter, object? ComputedColumns, int? SortOrder);
}
