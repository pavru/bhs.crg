using BHS.CRG.Infrastructure.Maintenance;

namespace BHS.CRG.Api.Endpoints.Maintenance;

/// <summary>
/// Разовые действия обслуживания — только администратор (issue #522).
///
/// Отдельно от EF-миграций осознанно: те применяются на старте приложения, когда блоб-хранилище
/// может быть недоступно, и получилось бы падающее или наполовину сконвертированное приложение.
/// Здесь момент выбирает человек и видит отчёт.
/// </summary>
public static class MaintenanceEndpoints
{
    public static void MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/maintenance").RequireAuthorization("Admin");

        // dryRun=true (по умолчанию) — только посчитать: сколько записей и картинок переедет и
        // сколько байт освободится в JSONB. Пересчёт безопасен и повторяем.
        g.MapPost("/images/migrate", async (
            ImageBlobMigration migration, bool? dryRun, CancellationToken ct) =>
        {
            var isDryRun = dryRun ?? true;
            var report = await migration.RunAsync(isDryRun, ct);
            return Results.Ok(new
            {
                report.Objects, report.Images, report.Bytes, report.Failed,
                report.Downscaled, report.SavedBytes, dryRun = isDryRun,
            });
        });

        // Дозаполнение человеческих имён материалов у связок, заведённых до появления метки (#561).
        // Читает и разбирает файлы наборов, поэтому тоже действие администратора, а не миграция.
        g.MapPost("/material-links/backfill-labels", async (
            MaterialLabelBackfill backfill, bool? dryRun, CancellationToken ct) =>
        {
            var isDryRun = dryRun ?? true;
            var report = await backfill.RunAsync(isDryRun, ct);
            return Results.Ok(new
            {
                report.LinksWithoutLabel, report.Named, report.NotFound,
                report.DocumentsScanned, report.DocumentsFailed, dryRun = isDryRun,
            });
        });
    }
}
