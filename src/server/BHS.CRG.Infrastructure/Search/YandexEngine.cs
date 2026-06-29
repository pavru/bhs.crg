using System.Net.Http.Headers;
using System.Xml.Linq;
using BHS.CRG.Application.Settings;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Search;

/// <summary>Движок веб-поиска через Яндекс XML (Yandex Cloud Search API). Настройки — из IIntegrationSettings.</summary>
public class YandexEngine(
    HttpClient http, IIntegrationSettings settings, ILogger<YandexEngine> logger
) : IWebSearchEngine
{
    public string Name => "Yandex";

    public async Task<IReadOnlyList<WebHit>> QueryAsync(string query, CancellationToken ct = default)
    {
        var cfg = (await settings.GetEffectiveAsync(ct)).Web("Yandex");
        var apiKey = cfg.ApiKey;
        var folderId = cfg.FolderId;
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(folderId)) return [];

        var host = string.IsNullOrWhiteSpace(cfg.Host) ? "https://yandex.ru/search/xml" : cfg.Host;

        // groupby: 10 групп по 1 документу (плоская выдача)
        const string groupby = "attr=d.mode=flat.groups-on-page=10.docs-in-group=1";
        var url = $"{host}?folderid={Uri.EscapeDataString(folderId)}&query={Uri.EscapeDataString(query)}&l10n=ru&sortby=rlv&filter=none&groupby={Uri.EscapeDataString(groupby)}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Api-Key", apiKey);
            var resp = await http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Yandex XML {Status}: {Body}", resp.StatusCode, body.Length > 200 ? body[..200] : body);
                return [];
            }

            var xml = XDocument.Parse(body);
            // ошибка уровня API
            var err = xml.Descendants("error").FirstOrDefault();
            if (err is not null)
            {
                logger.LogWarning("Yandex XML error: {Err}", err.Value);
                return [];
            }

            var list = new List<WebHit>();
            foreach (var docEl in xml.Descendants("doc"))
            {
                var link = docEl.Element("url")?.Value;
                if (string.IsNullOrWhiteSpace(link)) continue;
                var title = StripTags(docEl.Element("title"));
                var passage = StripTags(docEl.Element("passages")?.Element("passage")) is { Length: > 0 } p
                    ? p : StripTags(docEl.Element("headline"));
                list.Add(new WebHit(title, link, passage));
            }
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Yandex-запрос не выполнен");
            return [];
        }
    }

    /// <summary>Текст элемента с подсветкой &lt;hlword&gt; — берём как обычный текст.</summary>
    private static string StripTags(XElement? el) => el is null ? "" : string.Concat(el.Nodes().Select(NodeText)).Trim();

    private static string NodeText(XNode n) => n switch
    {
        XText t => t.Value,
        XElement e => string.Concat(e.Nodes().Select(NodeText)),
        _ => "",
    };
}
