using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Generation;

using BHS.CRG.Api.Endpoints.Common;

namespace BHS.CRG.Api.Endpoints.Attachments;

public static class AttachmentEndpoints
{
    public static void MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/attachments").RequireAuthorization();

        g.MapPost("/", async (IFormFile file, IBlobStorage blob, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (!AttachmentTypes.IsAccepted(file.ContentType))
                return Results.BadRequest(new { error = $"Формат не поддерживается: {file.ContentType}" });
            if (MismatchedExtension(file) is { } mismatch) return mismatch;
            if (UploadLimits.Exceeded(file, UploadLimits.Attachment) is { } tooLarge) return tooLarge;

            try
            {
                using var stream = file.OpenReadStream();
                var blobPath = await blob.UploadAsync(file.FileName, stream, file.ContentType, ct);
                return Results.Ok(new { blobPath, fileName = file.FileName, mimeType = file.ContentType, size = file.Length });
            }
            catch (Exception ex)
            {
                // Сообщение клиента SDK хранилища называет адрес, бакет и учётную запись — наружу
                // оно не идёт (то же правило, что у общего обработчика в Program.cs).
                loggers.CreateLogger("BHS.CRG.Attachments").LogError(ex, "Не удалось сохранить вложение");
                return Results.Problem(
                    detail: "Не удалось сохранить файл в хранилище. Обратитесь к администратору.",
                    title: "Ошибка загрузки файла",
                    statusCode: 500);
            }
        }).DisableAntiforgery();

        // Картинка поля-изображения (issue #523). Отдельно от общего вложения, потому что здесь
        // рождается ПРОИЗВОДНАЯ: оригинал кладём как есть, а рабочей делаем уменьшенную копию.
        // Оригинал остаётся — документы исполнительные, и «уменьшили без спроса и без возврата» для
        // печати недопустимо; иметь оригинал под рукой дороже пары мегабайт в хранилище.
        g.MapPost("/image", async (IFormFile file, IBlobStorage blob, ILoggerFactory loggers, CancellationToken ct) =>
        {
            // Тот же белый список, что у обычного вложения (issue #534): «image/*» пропускал бы tiff,
            // bmp, heic, avif — их декодер не понимает, картинка легла бы как есть, а отказ всплыл бы
            // только при генерации PDF, далеко от загрузки. ContentType бывает null, если часть
            // multipart пришла без заголовка, — тогда это тоже отказ, а не 500.
            var contentType = file.ContentType ?? "";
            if (!AttachmentTypes.IsImage(contentType))
                return Results.BadRequest(new { error = $"Это не поддерживаемое изображение: {contentType}" });
            // Сверка расширения нужна и здесь: путь короче (картинка ещё и уменьшается), но
            // оригинал ложится в хранилище тем же вызовом и с тем же именем.
            if (MismatchedExtension(file) is { } mismatch) return mismatch;
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
                loggers.CreateLogger("BHS.CRG.Attachments").LogError(ex, "Не удалось сохранить изображение");
                return Results.Problem(
                    detail: "Не удалось сохранить файл в хранилище. Обратитесь к администратору.",
                    title: "Ошибка загрузки файла", statusCode: 500);
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
                // Тип выводится из имени файла, а имя пришло от пользователя — поэтому таблица здесь
                // та же, что у приёма (AttachmentTypes): раньше это были два независимых списка, и
                // расширение одного молча оставляло второй прежним.
                return Results.File(stream, AttachmentTypes.ServeTypeFor(displayName), displayName);
            }
            catch
            {
                return Results.NotFound();
            }
        });
    }

    /// <summary>
    /// Отказ, если расширение имени не отвечает заявленному типу; иначе null.
    ///
    /// Заявленный тип приходит от клиента и ничем не подтверждён — не-браузерный клиент кладёт
    /// <c>evil.svg</c>, объявив его <c>image/png</c>. Прямо сейчас это не эксплуатируется: отдача
    /// выводит тип из расширения и вернёт <c>application/octet-stream</c>. Но тогда вся защита
    /// держится на одной карте расширений, а список приёма против такого клиента не даёт ничего —
    /// то есть рубеж здесь до сих пор был декоративным.
    /// </summary>
    private static IResult? MismatchedExtension(IFormFile file)
    {
        if (AttachmentTypes.ExtensionMatches(file.FileName, file.ContentType)) return null;

        var expected = AttachmentTypes.ExtensionsFor(file.ContentType);
        return Results.BadRequest(new
        {
            error = $"Имя файла «{file.FileName}» не отвечает заявленному типу {file.ContentType}: " +
                    $"ожидается {expected}.",
        });
    }
}
