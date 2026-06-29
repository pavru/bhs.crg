using BHS.CRG.Infrastructure.Backup;

namespace BHS.CRG.Api.Endpoints.Backup;

public static class BackupEndpoints
{
    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/backup").RequireAuthorization("Admin");

        g.MapGet("/", async (BackupService svc, CancellationToken ct) =>
        {
            var (stream, fileName) = await svc.ExportAsync(ct);
            return Results.File(stream, "application/zip", fileName);
        });

        g.MapPost("/restore", async (IFormFile file, BackupService svc, CancellationToken ct) =>
        {
            try
            {
                using var stream = file.OpenReadStream();
                var report = await svc.ImportAsync(stream, ct);
                return Results.Ok(report);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).DisableAntiforgery();
    }
}
