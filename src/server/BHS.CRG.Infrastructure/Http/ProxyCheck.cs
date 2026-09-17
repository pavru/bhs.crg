using System.Diagnostics;
using System.Net;
using BHS.CRG.Application.Settings;

namespace BHS.CRG.Infrastructure.Http;

/// <param name="Problem">Диагноз — тот же, которым объясняются рабочие отказы.</param>
/// <param name="Service">Сервис, до которого строили туннель; <c>null</c> — проверяли только сам прокси.</param>
/// <param name="Target">Адрес, до которого строили туннель, — чтобы ответ не пришлось разгадывать.</param>
public sealed record ProxyCheckResult(
    bool Ok, OutboundProblem Problem, string Message, OutboundService? Service, string? Target, int Milliseconds);

/// <summary>
/// Проверка прокси (issue #937): соединение с ним и туннель до одного из сервисов с галкой.
///
/// Ключ к проверке: ключ API не нужен. Ответ поставщика «не авторизован» (401/403) значит ровно то,
/// что нам и требуется, — запрос ДОШЁЛ. Проверять прокси настоящим рабочим запросом было бы и
/// дороже, и хуже: отказ по ключу неотличим от отказа прокси, а разделить их — вся задача.
/// </summary>
public static class ProxyCheck
{
    /// <summary>Сколько ждём. Проверку запускает человек и смотрит на кнопку — минуты здесь неуместны.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    /// <summary>
    /// Куда стучаться ради сервиса. Адреса — те же, по которым сервис работает: проверка чужого
    /// адреса проверяла бы чужой путь (прокси разрешает по хостам, и «gemini можно, github нельзя»
    /// — обычная настройка).
    /// </summary>
    public static Uri? TargetFor(OutboundService service, IntegrationSettingsModel m) => service switch
    {
        OutboundService.Gemini => new Uri("https://generativelanguage.googleapis.com/v1beta/models"),
        OutboundService.Anthropic => new Uri("https://api.anthropic.com/v1/models"),
        OutboundService.Serper => new Uri("https://google.serper.dev/search"),
        OutboundService.Yandex => Absolute(m.Web("Yandex").Host) ?? new Uri("https://yandex.ru/search/xml"),
        OutboundService.Ollama => Absolute(m.Rec("Ollama").BaseUrl) ?? new Uri("http://localhost:11434"),
        OutboundService.UpdateCheck or OutboundService.Github => new Uri("https://api.github.com/"),
        // Почта — не HTTP: её проверяет своя кнопка в разделе «Почта», и она тоже идёт через прокси.
        // У загрузки по внешним ссылкам постоянного адреса нет по определению.
        _ => null,
    };

    /// <summary>Сервисы, которые можно проверить туннелем, — в порядке предпочтения.</summary>
    public static IReadOnlyList<OutboundService> Checkable(OutboundProxyState state, IntegrationSettingsModel m)
        => [.. state.InUse.Where(s => TargetFor(s, m) is not null)];

    /// <summary>
    /// Проверяет прокси по ЗАДАННЫМ значениям (не по сохранённым): соединение, а если сервис
    /// назван — и туннель до него.
    /// </summary>
    /// <param name="timeout">Сколько ждать; по умолчанию <see cref="Timeout"/>. Мониторингу нужен
    /// срок короче: он ходит по кругу и не вправе занимать его собой.</param>
    public static async Task<ProxyCheckResult> RunAsync(
        ProxySettings proxy, OutboundService? service, Uri? target, CancellationToken ct, TimeSpan? timeout = null)
    {
        var limit = timeout ?? Timeout;
        if (!ProxySettings.TryParseUrl(proxy.Url, out var uri, out var error))
            return new ProxyCheckResult(false, OutboundProblem.None, error!, service, null, 0);

        var started = Stopwatch.GetTimestamp();
        int Elapsed() => (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        // Сначала сокет. Без этого «прокси не поднят» приходило бы тем же текстом, что и «прокси не
        // пустил»: отказ соединения вложен в отказ туннеля и виден только разбором.
        //
        // Подключаемся ОБЩИМ способом (OutboundConnect), а не голым Socket.ConnectAsync: за именем
        // может стоять несколько адресов, платформа перебирает их по очереди, и один недостижимый
        // съедает весь срок (issue #917). Поймано живьём: на «http://localhost:3128» проверка
        // рабочего прокси отвечала «не отвечает» через 12 секунд — ::1 не отзывался, до 127.0.0.1
        // очередь не доходила.
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(limit);
            using var socket = await OutboundConnect.ConnectAsync(
                new DnsEndPoint(uri!.IdnHost, uri.Port), filter: null, deadline.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new ProxyCheckResult(false, OutboundProblem.ProxyUnreachable,
                $"Прокси {uri} не отвечает: {OutboundDiagnosis.Mask(ex.Message)}", service, target?.ToString(), Elapsed());
        }

        if (service is not { } svc || target is null)
            return new ProxyCheckResult(true, OutboundProblem.None,
                $"Соединение с прокси {uri} установлено. Туннель не проверяли: ни у одного сервиса с постоянным адресом не стоит галка «Через прокси».",
                null, null, Elapsed());

        var state = OutboundProxyState.ForCheck(proxy, svc);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = limit };
        // Тем же способом подключения, что и рабочие клиенты (см. про несколько адресов выше): без
        // этого проверка рабочего прокси по имени «localhost» упиралась в срок и врала «не дошли».
        OutboundConnect.Apply(handler);
        OutboundProxy.RouteFixed(handler, uri!, state.Credential);
        using var client = new HttpClient(handler) { Timeout = limit };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            // Тот же User-Agent, что у проверки обновлений: GitHub без него отвечает 403, и
            // проверка обвиняла бы прокси в том, чего он не делал.
            req.Headers.UserAgent.ParseAdd("BHS.CRG-proxy-check");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var problem = OutboundDiagnosis.DiagnoseStatus((int)resp.StatusCode, svc, state);
            if (problem != OutboundProblem.None)
                return new ProxyCheckResult(false, problem, OutboundDiagnosis.Explain(problem, svc, state),
                    svc, target.ToString(), Elapsed());

            // Любой ответ сервиса — удача проверки: ключа мы не посылали, и 401/403 здесь значит
            // «дошли и нас узнали как чужого», то есть ровно то, что проверяли.
            return new ProxyCheckResult(true, OutboundProblem.None,
                $"Через прокси {uri} сервис «{OutboundProxy.DisplayName(svc)}» ответил {(int)resp.StatusCode}. " +
                "Для проверки этого достаточно: ключ не отправлялся, важно, что запрос дошёл.",
                svc, target.ToString(), Elapsed());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var problem = OutboundDiagnosis.Diagnose(ex, svc, state);
            var text = problem == OutboundProblem.None
                ? $"Через прокси {uri} до сервиса «{OutboundProxy.DisplayName(svc)}» дойти не удалось: {OutboundDiagnosis.Mask(ex.Message)}"
                : OutboundDiagnosis.Explain(problem, svc, state);
            return new ProxyCheckResult(false, problem, text, svc, target.ToString(), Elapsed());
        }
    }

    private static Uri? Absolute(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ? uri : null;
}
