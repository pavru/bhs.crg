using System.Net;

namespace BHS.CRG.Infrastructure.Http;

/// <summary>
/// GET по внешней ссылке с проверкой КАЖДОГО адреса на пути, включая перенаправления.
///
/// Перенаправления проходим сами. Автоматическое следование сводит любую проверку исходной ссылки на
/// нет: ответ с общедоступного хоста уводит куда угодно, а проверять там уже нечего и некому.
/// Поэтому клиентам, которые ходят по внешним ссылкам, автоследование выключено
/// (<c>AllowAutoRedirect = false</c> при регистрации), а цель каждого перехода проверяется заново.
/// </summary>
public static class SafeHttpGet
{
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, Uri uri, HttpCompletionOption completion,
        Action<HttpRequestMessage>? prepare = null, CancellationToken ct = default)
    {
        var current = uri;
        for (var hop = 0; ; hop++)
        {
            await OutboundAddressPolicy.EnsureAllowedAsync(current, ct);

            var req = new HttpRequestMessage(HttpMethod.Get, current);
            prepare?.Invoke(req);
            var resp = await http.SendAsync(req, completion, ct);

            if (!IsRedirect(resp.StatusCode) || resp.Headers.Location is null) return resp;

            // Ответ с перенаправлением дальше не нужен, а тело у него может быть — освобождаем.
            var location = resp.Headers.Location;
            resp.Dispose();

            if (hop >= OutboundAddressPolicy.MaxRedirects)
                throw new OutboundAddressRefusedException($"слишком много перенаправлений: {uri}");

            // Location бывает относительным — разрешаем относительно текущего адреса, иначе
            // проверять было бы нечего.
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (current.Scheme != Uri.UriSchemeHttp && current.Scheme != Uri.UriSchemeHttps)
                throw new OutboundAddressRefusedException($"перенаправление уводит со схемы http(s): {current}");
        }
    }

    private static bool IsRedirect(HttpStatusCode code) => code
        is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
