using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Infrastructure.Search;

/// <summary>Скачивает файл по ссылке для импорта скана в библиотеку (pdf/изображения, до 50 МБ).</summary>
public class HttpFileUrlFetcher(HttpClient http) : IFileUrlFetcher
{
    private const long MaxBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    { "application/pdf", "image/png", "image/jpeg", "image/jpg", "image/webp", "image/gif", "image/tiff" };

    public async Task<FetchedFile> FetchAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new SearchUnavailableException("Некорректная ссылка.");

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; BHS.CRG/1.0)");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new SearchUnavailableException($"Не удалось скачать файл ({(int)resp.StatusCode}).");

        var mime = resp.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        if (!Allowed.Contains(mime))
            throw new SearchUnavailableException($"Тип файла не поддерживается: {mime}. Нужны PDF или изображение.");

        if (resp.Content.Headers.ContentLength is { } len && len > MaxBytes)
            throw new SearchUnavailableException("Файл превышает 50 МБ.");

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > MaxBytes) throw new SearchUnavailableException("Файл превышает 50 МБ.");

        var fileName = DeriveFileName(uri, mime, resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName);
        return new FetchedFile(bytes, fileName, mime == "image/jpg" ? "image/jpeg" : mime);
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
