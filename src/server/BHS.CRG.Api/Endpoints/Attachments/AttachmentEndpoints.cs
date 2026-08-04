using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Generation;

using BHS.CRG.Api.Endpoints.Common;

namespace BHS.CRG.Api.Endpoints.Attachments;

public static class AttachmentEndpoints
{
    /// <summary>
    /// Что принимаем во вложения. Только форматы, которые браузер показывает как ДАННЫЕ:
    /// документы и растровые картинки.
    ///
    /// Активных форматов здесь быть не должно — вложение загружает один пользователь, а открывает
    /// другой, и открывает на домене приложения. Векторная графика для шаблонов идёт своим путём,
    /// через ассеты шаблонов (<c>TemplateAssetEndpoints</c>, роль Admin), — вложениям она не нужна.
    /// </summary>
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-excel",
        "image/png", "image/jpeg", "image/gif", "image/webp",
    };

    public static void MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/attachments").RequireAuthorization();

        g.MapPost("/", async (IFormFile file, IBlobStorage blob, CancellationToken ct) =>
        {
            if (!AllowedTypes.Contains(file.ContentType))
                return Results.BadRequest(new { error = $"Формат не поддерживается: {file.ContentType}" });
            if (UploadLimits.Exceeded(file, UploadLimits.Attachment) is { } tooLarge) return tooLarge;

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

        // Картинка поля-изображения (issue #523). Отдельно от общего вложения, потому что здесь
        // рождается ПРОИЗВОДНАЯ: оригинал кладём как есть, а рабочей делаем уменьшенную копию.
        // Оригинал остаётся — документы исполнительные, и «уменьшили без спроса и без возврата» для
        // печати недопустимо; иметь оригинал под рукой дороже пары мегабайт в хранилище.
        g.MapPost("/image", async (IFormFile file, IBlobStorage blob, CancellationToken ct) =>
        {
            // Тот же белый список, что у обычного вложения (issue #534): «image/*» пропускал бы tiff,
            // bmp, heic, avif — их декодер не понимает, картинка легла бы как есть, а отказ всплыл бы
            // только при генерации PDF, далеко от загрузки. ContentType бывает null, если часть
            // multipart пришла без заголовка, — тогда это тоже отказ, а не 500.
            var contentType = file.ContentType ?? "";
            if (!AllowedTypes.Contains(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Это не поддерживаемое изображение: {contentType}" });
            if (UploadLimits.Exceeded(file, UploadLimits.Attachment) is { } tooLarge) return tooLarge;

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var source = ms.ToArray();

            // Предел в байтах не ограничивает ПИКСЕЛИ: 10 МБ PNG разворачивается в гигабайты в
            // памяти (20000×20000 ≈ 1,6 ГБ), и любой вошедший пользователь мог бы уронить процесс.
            // Размеры читаем из заголовка, не декодируя (issue #534).
            if (ImageDownscaler.PixelCountExceeded(source) is { } tooManyPixels)
                return Results.BadRequest(new { error = tooManyPixels });

            string originalPath;
            try
            {
                originalPath = await blob.UploadAsync(
                    file.FileName, new MemoryStream(source), contentType, ct);
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, title: "Ошибка загрузки файла", statusCode: 500);
            }

            var down = ImageDownscaler.Downscale(source, file.ContentType);
            // Копию берём, ТОЛЬКО если она легче исходника. Уменьшение пикселей не гарантирует
            // уменьшения байтов: хорошо сжимаемый PNG (схема, скан с большими однотонными полями)
            // после пересжатия в JPEG может стать в разы ТЯЖЕЛЕЕ — поймано на живой проверке, где
            // 0,15 МБ превратились в 2,82 МБ. Смысл всей операции — вес, а не число пикселей.
            if (down.Bytes is not null && down.Bytes.Length >= source.Length) down = down with { Bytes = null };
            if (down.Bytes is null)
            {
                // Уменьшать не понадобилось (или формат не распознан) — рабочей остаётся сама
                // загруженная картинка, второй копии не заводим.
                return Results.Ok(new
                {
                    blobPath = originalPath, originalBlobPath = (string?)null,
                    fileName = file.FileName, mimeType = file.ContentType,
                    sourceBytes = source.LongLength, storedBytes = source.LongLength,
                });
            }

            var ext = down.MimeType == "image/png" ? "png" : "jpg";
            var smallName = Path.GetFileNameWithoutExtension(file.FileName) + "_" + down.Width + "." + ext;
            try
            {
                var smallPath = await blob.UploadAsync(smallName, new MemoryStream(down.Bytes), down.MimeType, ct);
                return Results.Ok(new
                {
                    blobPath = smallPath, originalBlobPath = originalPath,
                    fileName = file.FileName, mimeType = down.MimeType,
                    sourceBytes = source.LongLength, storedBytes = (long)down.Bytes.Length,
                });
            }
            catch (Exception)
            {
                // Копия не легла — оригинал уже в хранилище, и бросать его сиротой нельзя: он никому
                // не известен, удалить его потом будет некому. Работаем на оригинале (issue #534).
                return Results.Ok(new
                {
                    blobPath = originalPath, originalBlobPath = (string?)null,
                    fileName = file.FileName, mimeType = contentType,
                    sourceBytes = source.LongLength, storedBytes = source.LongLength,
                });
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
                // Тип выводится из имени файла, а имя пришло от пользователя — поэтому список здесь
                // держим не шире списка приёма. Всё незнакомое уходит как application/octet-stream:
                // так файл сохраняется, а не исполняется у того, кто его открыл. Расширения без
                // соответствия в AllowedTypes (в том числе загруженные, пока список был шире)
                // попадают именно в эту ветку.
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
