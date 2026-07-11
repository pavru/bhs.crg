using BHS.CRG.Application.Common;
using BHS.CRG.Application.Templates;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Templates;
using MediatR;

namespace BHS.CRG.Api.Endpoints.Templates;

public static class TemplateAssetEndpoints
{
    // Тип файла определяется расширением (не Content-Type — браузеры часто шлют
    // application/octet-stream для .ttf/.otf/.ttc), см. CLAUDE.md "типы файлов по возможностям Typst".
    private static readonly Dictionary<string, (TemplateAssetKind Kind, string MimeType)> ExtMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = (TemplateAssetKind.Image, "image/png"),
            [".jpg"] = (TemplateAssetKind.Image, "image/jpeg"),
            [".jpeg"] = (TemplateAssetKind.Image, "image/jpeg"),
            [".webp"] = (TemplateAssetKind.Image, "image/webp"),
            [".gif"] = (TemplateAssetKind.Image, "image/gif"),
            [".svg"] = (TemplateAssetKind.Image, "image/svg+xml"),
            [".ttf"] = (TemplateAssetKind.Font, "font/ttf"),
            [".otf"] = (TemplateAssetKind.Font, "font/otf"),
            [".ttc"] = (TemplateAssetKind.Font, "font/collection"),
        };

    public static void MapTemplateAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/template-assets").RequireAuthorization();
        var admin = app.MapGroup("/api/template-assets").RequireAuthorization("Admin");

        g.MapGet("/", async (TemplateAssetScope scope, Guid? scopeId, IMediator m, CancellationToken ct) =>
            Results.Ok(await m.Send(new ListTemplateAssetsQuery(scope, scopeId), ct)));

        admin.MapPost("/", async (IFormFile file, TemplateAssetScope scope, Guid? scopeId, string name,
            IBlobStorage blob, IMediator m, CancellationToken ct) =>
        {
            var ext = Path.GetExtension(file.FileName);
            if (!ExtMap.TryGetValue(ext, out var info))
                return Results.BadRequest(new { error = $"Формат не поддерживается: {ext}" });
            if (file.Length > 20 * 1024 * 1024)
                return Results.BadRequest(new { error = "Файл превышает 20 МБ" });
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "Укажите имя ассета" });

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }
            string? fontFamilyName = info.Kind == TemplateAssetKind.Font
                ? FontFamilyNameReader.TryReadFamilyName(bytes)
                : null;

            using var uploadStream = new MemoryStream(bytes);
            var blobPath = await blob.UploadAsync(file.FileName, uploadStream, info.MimeType, ct);
            var asset = await m.Send(new CreateTemplateAssetCommand(
                scope, scopeId, info.Kind, name.Trim(), file.FileName, info.MimeType, blobPath, fontFamilyName), ct);
            return Results.Ok(asset);
        }).DisableAntiforgery();

        admin.MapPut("/{id:guid}", async (Guid id, IFormFile file, IBlobStorage blob, IMediator m, CancellationToken ct) =>
        {
            var ext = Path.GetExtension(file.FileName);
            if (!ExtMap.TryGetValue(ext, out var info))
                return Results.BadRequest(new { error = $"Формат не поддерживается: {ext}" });
            if (file.Length > 20 * 1024 * 1024)
                return Results.BadRequest(new { error = "Файл превышает 20 МБ" });

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms, ct);
                bytes = ms.ToArray();
            }
            string? fontFamilyName = info.Kind == TemplateAssetKind.Font
                ? FontFamilyNameReader.TryReadFamilyName(bytes)
                : null;

            using var uploadStream = new MemoryStream(bytes);
            var blobPath = await blob.UploadAsync(file.FileName, uploadStream, info.MimeType, ct);
            try
            {
                var asset = await m.Send(new ReplaceTemplateAssetCommand(id, file.FileName, info.MimeType, blobPath, fontFamilyName), ct);
                return Results.Ok(asset);
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        }).DisableAntiforgery();

        admin.MapDelete("/{id:guid}", async (Guid id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new DeleteTemplateAssetCommand(id), ct);
            return Results.NoContent();
        });
    }
}
