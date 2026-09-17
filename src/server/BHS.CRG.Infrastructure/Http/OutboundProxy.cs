using System.Net;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Common;
using MailKit.Net.Proxy;

namespace BHS.CRG.Infrastructure.Http;

/// <summary>
/// Внешний сервис, у которого в настройках есть галка «через прокси» (issue #936).
///
/// Прокси выбирается по СЕРВИСУ, а не по адресу запроса. По адресу их не различить: проверка
/// обновлений и отправка сообщений в GitHub ходят на один хост под разными галками, а адреса Ollama
/// и Яндекса настраиваются. Поэтому сервис — свойство клиента: у каждого сервиса свой именованный
/// клиент, и тест (<c>OutboundClientRoutingTests</c>) не даёт зарегистрировать клиент, не сказав,
/// чей он.
/// </summary>
public enum OutboundService
{
    Gemini,
    Anthropic,
    Ollama,
    Serper,
    Yandex,
    Smtp,
    UpdateCheck,
    Github,
    /// <summary>Страницы из выдачи поиска и файлы по ссылке — адреса произвольные, см. OutboundAddressPolicy.</summary>
    ExternalLinks,
}

/// <summary>
/// Текущий прокси и то, кто им пользуется. Один на процесс; обновляется при каждой сборке
/// эффективных настроек (<c>IntegrationSettingsService</c>), то есть смена прокси и галок действует
/// без перезапуска.
///
/// Читается на КАЖДОМ запросе (<see cref="ServiceWebProxy"/>), поэтому в базу не ходит: держит
/// снимок, подменяемый целиком одним присваиванием.
/// </summary>
public sealed class OutboundProxyState
{
    private sealed record Snapshot(Uri? Proxy, NetworkCredential? Credential, IReadOnlySet<OutboundService> Services);

    private volatile Snapshot _current = new(null, null, new HashSet<OutboundService>());

    /// <summary>
    /// Состояние «этот прокси, для этих сервисов» — для проверки связи (issue #937). Она идёт по
    /// значениям ФОРМЫ, ещё не сохранённым, и разбирать её отказы должен тот же классификатор, что
    /// разбирает рабочие: иначе кнопка и работа объясняли бы одну беду по-разному.
    /// </summary>
    public static OutboundProxyState ForCheck(ProxySettings proxy, params OutboundService[] services)
    {
        ProxySettings.TryParseUrl(proxy.Url, out var uri, out _);
        var state = new OutboundProxyState();
        state._current = new Snapshot(uri, CredentialOf(proxy, uri), services.ToHashSet());
        return state;
    }

    private static NetworkCredential? CredentialOf(ProxySettings proxy, Uri? uri)
        => uri is not null && !string.IsNullOrWhiteSpace(proxy.User)
            ? new NetworkCredential(proxy.User.Trim(), proxy.Password ?? "")
            : null;

    public void Update(IntegrationSettingsModel m)
    {
        ProxySettings.TryParseUrl(m.Proxy.Url, out var uri, out _);
        var credential = CredentialOf(m.Proxy, uri);

        var services = new HashSet<OutboundService>();
        void Mark(bool on, OutboundService s) { if (on) services.Add(s); }
        Mark(m.Rec("Gemini").UseProxy, OutboundService.Gemini);
        Mark(m.Rec("Anthropic").UseProxy, OutboundService.Anthropic);
        Mark(m.Rec("Ollama").UseProxy, OutboundService.Ollama);
        Mark(m.Web("Serper").UseProxy, OutboundService.Serper);
        Mark(m.Web("Yandex").UseProxy, OutboundService.Yandex);
        Mark(m.Smtp.UseProxy, OutboundService.Smtp);
        Mark(m.Updates.UseProxy, OutboundService.UpdateCheck);
        Mark(m.Github.UseProxy, OutboundService.Github);
        Mark(m.ExternalLinksUseProxy, OutboundService.ExternalLinks);

        _current = new Snapshot(uri, credential, services);
    }

    /// <summary>Прокси для сервиса; <c>null</c> — напрямую (галка снята или прокси не задан).</summary>
    public Uri? ProxyFor(OutboundService service)
    {
        var s = _current;
        return s.Proxy is not null && s.Services.Contains(service) ? s.Proxy : null;
    }

    public NetworkCredential? Credential => _current.Credential;

    /// <summary>Заданный прокси, независимо от галок; <c>null</c> — не задан.</summary>
    public Uri? Configured => _current.Proxy;

    /// <summary>Сервисы, которые сейчас ходят через прокси. Пусто — прокси не задан или галок нет.</summary>
    public IReadOnlyList<OutboundService> InUse
    {
        get
        {
            var s = _current;
            return s.Proxy is null ? [] : [.. s.Services.Order()];
        }
    }

    /// <summary>
    /// Соединение к <paramref name="host"/> — это соединение с прокси сервиса, а не с целью запроса?
    /// Нужно проверке адреса: при прокси подключение открывается к НЕМУ, и проверять публичность
    /// надо не этот адрес (см. <see cref="OutboundAddressPolicy.ApplyGuard(System.Net.Http.SocketsHttpHandler, OutboundProxyState)"/>).
    /// </summary>
    public bool IsProxyHostFor(OutboundService service, string host)
        => ProxyFor(service) is { } proxy
           && (string.Equals(proxy.IdnHost, host, StringComparison.OrdinalIgnoreCase)
               || string.Equals(proxy.Host, host, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// <see cref="IWebProxy"/> одного сервиса. Обработчик спрашивает его на каждый запрос, и пул
/// соединений ключуется выданным адресом прокси, — поэтому смена прокси или галки действует на
/// следующий запрос без пересоздания клиентов.
/// </summary>
public sealed class ServiceWebProxy(OutboundService service, OutboundProxyState state) : IWebProxy
{
    public OutboundService Service => service;

    /// <summary>
    /// Учётные данные — объектом, который смотрит в состояние при каждом обращении. Обработчик
    /// может запомнить ссылку на <c>Credentials</c> один раз, и тогда смена пароля без этого
    /// не доходила бы до новых соединений до перезапуска.
    /// </summary>
    public ICredentials? Credentials
    {
        get => new LiveCredentials(state);
        set { }
    }

    public Uri? GetProxy(Uri destination) => state.ProxyFor(service);

    public bool IsBypassed(Uri host) => state.ProxyFor(service) is null;

    private sealed class LiveCredentials(OutboundProxyState state) : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType) => state.Credential;
    }
}

/// <summary>Как клиенты подключаются к прокси. Одно место на всё приложение.</summary>
public static class OutboundProxy
{
    /// <summary>Клиент сервиса ходит через прокси, если у сервиса стоит галка.</summary>
    public static void Route(SocketsHttpHandler handler, OutboundService service, OutboundProxyState state)
    {
        handler.UseProxy = true;
        handler.Proxy = new ServiceWebProxy(service, state);
    }

    /// <summary>
    /// Напрямую, что бы ни было в окружении. Умолчание для всех клиентов фабрики: хранилище, Ollama
    /// рядом, плагины — внутренние по замыслу, и в прокси им нельзя.
    /// </summary>
    public static void Direct(SocketsHttpHandler handler)
    {
        handler.UseProxy = false;
        handler.Proxy = null;
    }

    /// <summary>
    /// Через ЭТОТ прокси, а не через настроенный. Нужно проверке связи: её запускают по значениям
    /// формы, ещё не сохранённым, — иначе проверялось бы не то, что человек набрал.
    /// </summary>
    public static void RouteFixed(SocketsHttpHandler handler, Uri proxy, NetworkCredential? credential)
    {
        handler.UseProxy = true;
        handler.Proxy = new WebProxy(proxy) { Credentials = credential };
    }

    /// <summary>Имя именованного клиента: назначение и сервис. Одна функция, чтобы регистрация и вызов не разъехались.</summary>
    public static string ClientName(string purpose, OutboundService service) => $"{purpose}:{service}";

    /// <summary>Имя сервиса для человека: оно попадает и в тексты отказов, и в строки состояния.</summary>
    public static string DisplayName(OutboundService service) => service switch
    {
        OutboundService.Gemini => "Gemini",
        OutboundService.Anthropic => "Anthropic",
        OutboundService.Ollama => "Ollama",
        OutboundService.Serper => "Serper",
        OutboundService.Yandex => "Яндекс",
        OutboundService.Smtp => "Почта",
        OutboundService.UpdateCheck => "Проверка обновлений",
        OutboundService.Github => "Передача в GitHub",
        OutboundService.ExternalLinks => "Загрузка по внешним ссылкам",
        _ => service.ToString(),
    };

    /// <summary>
    /// Прокси для MailKit. Почта ходит не HTTP-клиентом, поэтому своя трансляция — но из того же
    /// состояния. Решение «через прокси» принимает вызывающий: проверка связи идёт по галке из формы,
    /// ещё не сохранённой. <c>null</c> — прокси не задан.
    /// </summary>
    public static IProxyClient? ForMailKit(OutboundProxyState state)
    {
        if (state.Configured is not { } proxy) return null;
        var credential = state.Credential;
        return proxy.Scheme switch
        {
            "http" => credential is null ? new HttpProxyClient(proxy.Host, proxy.Port) : new HttpProxyClient(proxy.Host, proxy.Port, credential),
            "socks4" => credential is null ? new Socks4Client(proxy.Host, proxy.Port) : new Socks4Client(proxy.Host, proxy.Port, credential),
            "socks4a" => credential is null ? new Socks4aClient(proxy.Host, proxy.Port) : new Socks4aClient(proxy.Host, proxy.Port, credential),
            "socks5" => credential is null ? new Socks5Client(proxy.Host, proxy.Port) : new Socks5Client(proxy.Host, proxy.Port, credential),
            _ => throw new InvalidRequestException($"Схема прокси «{proxy.Scheme}» для почты не поддерживается. Допустимы: {string.Join(", ", ProxySettings.Schemes)}."),
        };
    }

    /// <summary>Переменные, которыми .NET, curl, Typst и прочие берут прокси из окружения.</summary>
    public static readonly string[] EnvironmentVariables =
        ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy"];
}
