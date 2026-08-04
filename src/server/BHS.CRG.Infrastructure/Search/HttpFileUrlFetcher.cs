using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Infrastructure.Http;

namespace BHS.CRG.Infrastructure.Search;

/// <summary>Скачивает файл по ссылке для импорта скана в библиотеку (pdf/изображения, до 50 МБ).</summary>
public class HttpFileUrlFetcher(HttpClient http) : IFileUrlFetcher
{
    private const long MaxBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    { "application/pdf", "image/png", "image/jpeg", "image/jpg", "image/webp", "image/gif", "image/tiff" };

    /// <summary>
    /// Ссылку сюда приносит обычный пользователь, поэтому адрес назначения проверяется политикой
    /// исходящих запросов — включая цель каждого перенаправления (см. <see cref="SafeHttpGet"/>).
    ///
    /// Отказы сетевого слоя и по адресу отвечают ОДНИМ текстом. Прежде они различались («404»,
    /// «тип не поддерживается: text/html», молчание до таймаута), и по этой разнице внутреннюю сеть
    /// можно было нарисовать, ничего не скачав. Причина уходит в исключение для лога, наружу — один
    /// ответ. Отказ по типу и по размеру остались своими: они про сам файл и ничего о сети не говорят.
    /// </summary>
    public async Task<FetchedFile> FetchAsync(string url, CancellationToken ct = default)
    {
        Uri uri;
        HttpResponseMessage resp;
        try
        {
            uri = OutboundAddressPolicy.RequireHttpUrl(url);
            resp = await SafeHttpGet.SendAsync(
                http, uri, HttpCompletionOption.ResponseHeadersRead,
                req => req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; BHS.CRG/1.0)"),
                ct);
        }
        catch (OutboundAddressRefusedException)
        {
            throw new SearchUnavailableException(OutboundAddressPolicy.RefusalMessage);
        }
        catch (HttpRequestException)
        {
            throw new SearchUnavailableException(OutboundAddressPolicy.RefusalMessage);
        }
        // Молчание до истечения срока — такой же различимый ответ, как «404» или «не тот тип»:
        // по нему видно, что по адресу кто-то есть, но не отвечает. Отдаём общий отказ.
        // Сюда же битый относительный Location, из-за которого разбор адреса бросает своё.
        catch (Exception e) when (e is TaskCanceledException or TimeoutException or UriFormatException
                                  && !ct.IsCancellationRequested)
        {
            throw new SearchUnavailableException(OutboundAddressPolicy.RefusalMessage);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new SearchUnavailableException(OutboundAddressPolicy.RefusalMessage);

            var mime = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            if (!Allowed.Contains(mime))
                throw new SearchUnavailableException($"Тип файла не поддерживается: {mime}. Нужны PDF или изображение.");

            // Заголовок длины — только ранний отказ: у ответа с потоковой передачей его нет вовсе,
            // и полагаться на него значит не иметь предела там, где он нужнее всего.
            if (resp.Content.Headers.ContentLength is { } len && len > MaxBytes)
                throw new SearchUnavailableException("Файл превышает 50 МБ.");

            var bytes = await ReadWithLimitAsync(resp, ct);
            var fileName = DeriveFileName(uri, mime,
                resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName);
            return new FetchedFile(bytes, fileName, mime == "image/jpg" ? "image/jpeg" : mime);
        }
    }

    /// <summary>
    /// Читает тело потоком и обрывается на пределе. Прежде тело целиком буферизовалось в память и
    /// проверялось ПОСЛЕ — то есть предел не мешал занять память по размеру ответа, а у передачи без
    /// заявленной длины не мешал ничему вовсе.
    /// </summary>
    private static async Task<byte[]> ReadWithLimitAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var source = await resp.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                throw new SearchUnavailableException("Файл превышает 50 МБ.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string DeriveFileName(Uri uri, string mime, string? disposition)
    {
        var name = disposition?.Trim('"');
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(name)) name = "document";
        }
        if (!Path.HasExtension(name))
        {
            var ext = mime switch
            {
                "application/pdf" => ".pdf",
                "image/png" => ".png",
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                "image/tiff" => ".tiff",
                _ => "",
            };
            name += ext;
        }
        return name;
    }
}
