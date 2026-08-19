using BHS.CRG.Infrastructure.Http;
using System.Net;
using System.Text.RegularExpressions;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Application.Settings;

namespace BHS.CRG.Infrastructure.Search;

/// <summary>
/// Веб-поиск документов качества: тиры ФГИС → производитель → веб, агрегируя выдачу
/// всех ВКЛЮЧЁННЫХ движков (Serper, Яндекс). Дополнительно заходит на найденные страницы
/// и извлекает из их HTML прямые ссылки на файлы документов (часто поисковик отдаёт
/// страницу изделия, а ссылка на сам PDF/сертификат есть только в её разметке).
/// </summary>
public class TieredWebSearch(IEnumerable<IWebSearchEngine> engines, IIntegrationSettings settings, HttpClient http) : IQualityDocSearch
{
    private const int PagesToScan = 6;     // сколько найденных страниц «раскрываем» в поисках файлов
    private const int MaxLinksPerPage = 15;

    public async Task<IReadOnlyList<SearchCandidate>> SearchAsync(string query, CancellationToken ct = default)
    {
        var s = await settings.GetEffectiveAsync(ct);
        var active = engines.Where(e => EngineReadiness.IsUsableForWebSearch(e.Name, s.Web(e.Name))).ToList();
        if (active.Count == 0)
            throw new SearchUnavailableException("Веб-поиск не настроен (нет включённых провайдеров с ключами). Проверьте «Настройки → Поиск и распознавание».");

        var fgis = s.FgisDomains.Count > 0 ? s.FgisDomains.ToArray() : ["pub.fsa.gov.ru", "fsa.gov.ru"];
        var manufacturers = s.ManufacturerDomains.ToArray();

        var docTerms = "(сертификат соответствия OR декларация соответствия)";
        var tiers = new List<(string Source, string Q)>
        {
            ("file", $"{query} {docTerms} filetype:pdf"),
        };
        if (fgis.Length > 0) tiers.Add(("fgis", $"{query} {SiteFilter(fgis)}"));
        if (manufacturers.Length > 0) tiers.Add(("manufacturer", $"{query} сертификат {SiteFilter(manufacturers)}"));
        tiers.Add(("web", $"{query} {docTerms}"));

        var tasks = new List<Task<(string Source, IReadOnlyList<WebHit>? Hits)>>();
        foreach (var tier in tiers)
            foreach (var engine in active)
                tasks.Add(Run(engine, tier.Source, tier.Q, ct));
        var results = await Task.WhenAll(tasks);

        // Ни один запрос ни к одному движку не ответил — это отказ поиска, а не отсутствие
        // результатов (issue #797). Пустой список здесь означал бы «ничего не найдено», и полная
        // недоступность выглядела бы для пользователя обычной пустой выдачей.
        if (Array.TrueForAll(results, r => r.Hits is null))
            throw new SearchUnavailableException(
                "Ни один движок веб-поиска не ответил. Проверьте подключение и «Настройки → Поиск и распознавание».");

        var all = results.Where(r => r.Hits is not null).Select(r => (r.Source, Hits: r.Hits!)).ToArray();

        var order = new Dictionary<string, int> { ["file"] = 0, ["fgis"] = 1, ["manufacturer"] = 2, ["web"] = 3 };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<SearchCandidate>();
        foreach (var grp in all.OrderBy(r => order.GetValueOrDefault(r.Source, 9)))
            foreach (var h in grp.Hits)
                if (seen.Add(NormalizeUrl(h.Url)))
                    merged.Add(new SearchCandidate(h.Title, h.Url, h.Snippet, IsFileUrl(h.Url) ? "file" : grp.Source));

        // Раскрываем верхние HTML-страницы и достаём прямые ссылки на файлы документов.
        var pages = merged.Where(c => !IsFileUrl(c.Url)).Take(PagesToScan).ToList();
        var scraped = await Task.WhenAll(pages.Select(p => ScrapeDocLinksAsync(p.Url, ct)));
        foreach (var link in scraped.SelectMany(x => x))
            if (seen.Add(NormalizeUrl(link.Url)))
                merged.Add(link);

        // Файлы — в начало; среди файлов приоритет по типу документа (сертификат/декларация/отказное → выше).
        return merged
            .OrderBy(c => IsFileUrl(c.Url) ? 0 : 1)
            .ThenBy(c => DocRank($"{c.Title} {c.Url}"))
            .Take(40).ToList();
    }

    // Приоритет по именованию документа: чем меньше — тем выше.
    private static readonly string[] PriorityDoc =
        ["сертификат", "certificate", "sertifikat", "деклараци", "declaration", "deklarac", "отказ"];
    private static readonly string[] SecondaryDoc =
        ["паспорт", "passport", "pasport", "свидетельств", "протокол"];
    private static int DocRank(string s)
    {
        s = WebUtility.UrlDecode(s).ToLowerInvariant();
        if (PriorityDoc.Any(k => s.Contains(k, StringComparison.Ordinal))) return 0;
        if (SecondaryDoc.Any(k => s.Contains(k, StringComparison.Ordinal))) return 1;
        return 2;
    }

    private static readonly string[] FileExtensions =
        [".pdf", ".doc", ".docx", ".rtf", ".jpg", ".jpeg", ".png", ".tif", ".tiff"];
    private static readonly string[] DocExtensions = [".pdf", ".doc", ".docx", ".rtf"];
    // Ключевые слова, по которым со страницы отбираем именно документы качества (а не футерные PDF).
    private static readonly string[] DocKeywords =
    [
        "сертификат", "деклараци", "паспорт", "качеств", "соответстви", "протокол", "свидетельств",
        "conformity", "certificate", "declaration", "sertifikat", "deklarac", "pasport", "passport",
    ];
    private static bool LooksLikeQualityDoc(string s)
    {
        s = s.ToLowerInvariant();
        return DocKeywords.Any(k => s.Contains(k, StringComparison.Ordinal));
    }

    private static bool HasExt(string url, string[] exts)
    {
        var path = url;
        var q = path.IndexOfAny(['?', '#']);
        if (q >= 0) path = path[..q];
        return exts.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsFileUrl(string url) => HasExt(url, FileExtensions);

    /// <summary>Скачивает HTML страницы и достаёт ссылки на файлы документов (pdf/doc/...).</summary>
    private async Task<IReadOnlyList<SearchCandidate>> ScrapeDocLinksAsync(string pageUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri)) return [];
        string html;
        try
        {
            // Адрес приходит из выдачи поисковика, то есть снаружи, — проверяем его и цель каждого
            // перенаправления так же, как при загрузке файла по ссылке.
            using var resp = await SafeHttpGet.SendAsync(
                http, baseUri, HttpCompletionOption.ResponseContentRead, ct: ct);
            if (!resp.IsSuccessStatusCode) return [];
            var ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (!ctype.Contains("html", StringComparison.OrdinalIgnoreCase)) return [];
            html = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (!HttpFailure.IsUserCancellation(ex, ct))
        {
            // Страница не раскрылась — не беда, ссылки на файлы поищутся на остальных. Но ОТМЕНУ
            // пробрасываем: голый `catch` глотал и её, и запрос, который никому не нужен, дочитывал
            // страницы до конца (issue #797).
            return [];
        }

        var found = new List<SearchCandidate>();
        var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(html, "<a\\b[^>]*?href\\s*=\\s*[\"']([^\"']+)[\"'][^>]*>(.*?)</a>",
                     RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var href = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            if (href.Length == 0 || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(baseUri, href, out var abs)) continue;
            if (!HasExt(abs.AbsolutePath, DocExtensions)) continue; // только файлы документов, не картинки/страницы

            var url = abs.ToString();
            if (!local.Add(NormalizeUrl(url))) continue;
            var text = StripTags(WebUtility.HtmlDecode(m.Groups[2].Value)).Trim();
            // Берём только ссылки, похожие на документ качества (по тексту ссылки или URL) — отсекаем футерные PDF.
            if (!LooksLikeQualityDoc($"{text} {WebUtility.UrlDecode(abs.AbsolutePath)}")) continue;
            if (text.Length == 0) text = Path.GetFileName(abs.AbsolutePath);
            found.Add(new SearchCandidate(text, url, $"С страницы {baseUri.Host}", "file"));
        }
        // Приоритет по типу документа (сертификат/декларация/отказное → выше), затем обрезаем по лимиту.
        return found.OrderBy(c => DocRank($"{c.Title} {c.Url}")).Take(MaxLinksPerPage).ToList();
    }

    private static string StripTags(string s) => Regex.Replace(s, "<[^>]+>", " ").Replace(" ", " ").Trim();

    /// <summary>
    /// Один запрос к одному движку. Отказ движка (таймаут, сетевой сбой, ошибка API) — <c>null</c>,
    /// НЕ пустой список: пустой означал бы «ничего не нашлось», и различить их выше было бы нечем.
    /// Отказ одного движка поиск не роняет — на то и агрегирование выдачи нескольких.
    /// </summary>
    private static async Task<(string, IReadOnlyList<WebHit>?)> Run(IWebSearchEngine e, string source, string q, CancellationToken ct)
    {
        try { return (source, await e.QueryAsync(q, ct)); }
        catch (SearchUnavailableException) { return (source, null); }
    }

    private static string SiteFilter(string[] domains) => "(" + string.Join(" OR ", domains.Select(d => $"site:{d}")) + ")";
    private static string NormalizeUrl(string url) => url.TrimEnd('/').ToLowerInvariant();
}
