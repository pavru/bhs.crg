using BHS.CRG.Application.Common;

namespace BHS.CRG.Api.Endpoints.Attachments;

public static class AttachmentEndpoints
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-excel",
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/svg+xml",
    };

    public static void MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/attachments").RequireAuthorization();

        g.MapPost("/", async (IFormFile file, IBlobStorage blob, CancellationToken ct) =>
        {
            if (!AllowedTypes.Contains(file.ContentType))
                return Results.BadRequest(new { error = $"Формат не поддерживается: {file.ContentType}" });
            if (file.Length > 50 * 1024 * 1024)
                return Results.BadRequest(new { error = "Файл превышает 50 МБ" });

            try
            {
                using var stream = file.OpenReadStream();
                var blobPath = await blob.UploadAsync(file.FileName, stream, file.ContentType, ct);
                return Results.Ok(new { blobPath, fileName = file.FileName, mimeType = file.ContentType, size = file.Length });
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    detail: ex.Message,
                    title: "Ошибка загрузки файла",
                    statusCode: 500);
            }
        }).DisableAntiforgery();

        g.MapGet("/", async (string path, IBlobStorage blob, CancellationToken ct) =>
        {
            try
            {
                var stream = await blob.DownloadAsync(path, ct);
                var segment = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
                var underscoreIdx = segment.IndexOf('_');
                var displayName = underscoreIdx >= 0 ? segment[(underscoreIdx + 1)..] : segment;
                var ext = Path.GetExtension(displayName).TrimStart('.').ToLowerInvariant();
                var contentType = ext switch
                {
                    "pdf"  => "application/pdf",
                    "docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    "xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    "xls"  => "application/vnd.ms-excel",
                    "png"  => "image/png",
                    "jpg" or "jpeg" => "image/jpeg",
                    "gif"  => "image/gif",
                    "webp" => "image/webp",
                    "svg"  => "image/svg+xml",
                    _      => "application/octet-stream",
                };
                return Results.File(stream, contentType, displayName);
            }
            catch
            {
                return Results.NotFound();
            }
        });
    }
}
