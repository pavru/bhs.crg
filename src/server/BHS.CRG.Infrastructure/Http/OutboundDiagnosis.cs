using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.RegularExpressions;

namespace BHS.CRG.Infrastructure.Http;

/// <summary>Чем на самом деле кончился запрос наружу, когда в пути стоит прокси (issue #937).</summary>
public enum OutboundProblem
{
    /// <summary>Про прокси сказать нечего — отказ обычный, и объясняет его сам сервис.</summary>
    None,

    /// <summary>До прокси не достучались: имя не разрешилось, порт закрыт, соединение не приняли.</summary>
    ProxyUnreachable,

    /// <summary>Прокси требует вход или не принял логин с паролем (407, отказ SOCKS в аутентификации).</summary>
    ProxyAuth,

    /// <summary>Прокси запретил ходить именно туда (403 на CONNECT, отказ правилами).</summary>
    ProxyForbidden,

    /// <summary>Прокси принял запрос, но сам не достучался до сервиса (502/503/504).</summary>
    ProxyUpstream,

    /// <summary>За прокси стоит чужой сертификат — обычно проверка TLS на корпоративном прокси.</summary>
    ProxyTlsIntercepted,

    /// <summary>Напрямую не вышло, а прокси в системе есть — просто у этого сервиса галка снята.</summary>
    ProxyOffForService,
}

/// <summary>
/// Один классификатор отказов на всех, кто ходит наружу: мониторинг, распознавание, проверка
/// обновлений, кнопка проверки прокси (issue #937).
///
/// Нужен потому, что с прокси отказы перестают говорить правду о себе: «сервис недоступен» приходит
/// и когда сервис жив, а прокси не пустил, и когда прокси не поднят вовсе, и когда галка «через
/// прокси» у этого сервиса просто не стоит. Разобрать это по тексту исключения может только тот, кто
/// знает, какая сторона соединения чья, — поэтому разбор живёт здесь, а не в каждом движке.
///
/// Правило разбора одно: при прокси соединение открывается К ПРОКСИ, а не к цели. Значит отказ
/// соединения, разбора имени и рукопожатия TLS до туннеля — это отказ ПРОКСИ, и утверждать это можно
/// твёрдо, а не гадая по словам в сообщении.
/// </summary>
public static class OutboundDiagnosis
{
    /// <summary>Логин с паролем в адресе — прячем перед записью в уведомление, журнал или ответ.</summary>
    private static readonly Regex Credentials = new(@"//[^/\s@]+:[^/\s@]+@", RegexOptions.Compiled);

    /// <summary>Код ответа прокси на CONNECT: .NET кладёт его в сообщение в кавычках.</summary>
    private static readonly Regex TunnelStatus = new(@"'(\d{3})'", RegexOptions.Compiled);

    /// <summary>
    /// Прячет <c>логин:пароль@</c> в тексте. Наши настройки такой адрес не принимают (см.
    /// <c>ProxySettings.TryParseUrl</c>), но текст сюда приходит и от чужого кода — от .NET, от
    /// MailKit, от самого прокси, — а уведомление читают люди без права на этот пароль.
    /// </summary>
    public static string Mask(string? text) => text is null ? "" : Credentials.Replace(text, "//***:***@");

    /// <summary>Отказ одной фразой: диагноз про прокси, если он есть, иначе исходный текст без секретов.</summary>
    public static string Describe(Exception ex, OutboundService service, OutboundProxyState state)
    {
        var problem = Diagnose(ex, service, state);
        if (problem != OutboundProblem.None)
            return $"{Explain(problem, service, state)} ({Mask(ex.Message)})";

        // Сказать про прокси нечего — но упомянуть, что путь шёл через него, обязаны: иначе разбор
        // начнётся с сервиса, к которому запрос, возможно, и не попадал.
        return state.ProxyFor(service) is { } proxy
            ? $"Через прокси {Mask(proxy.ToString())}: {Mask(ex.Message)}"
            : Mask(ex.Message);
    }

    /// <summary>
    /// Что случилось на самом деле. <see cref="OutboundProblem.None"/> — ничего про прокси сказать
    /// нельзя, и выдумывать нечего: отказ объясняет сам сервис.
    /// </summary>
    public static OutboundProblem Diagnose(Exception ex, OutboundService service, OutboundProxyState state)
    {
        if (state.ProxyFor(service) is null)
            // Прокси задан, а у этого сервиса галки нет. Это диагноз только для отказа СОЕДИНЕНИЯ:
            // ответ сервиса с кодом ошибки к прокси отношения не имеет, и подсказка про галку там
            // была бы ложным следом.
            return state.Configured is not null && IsConnectFailure(ex)
                ? OutboundProblem.ProxyOffForService
                : OutboundProblem.None;

        foreach (var e in Chain(ex))
        {
            if (e is HttpRequestException http)
            {
                if (http.HttpRequestError == HttpRequestError.ProxyTunnelError)
                    return FromTunnel(http);
                if (http.HttpRequestError == HttpRequestError.SecureConnectionError)
                    // Рукопожатие TLS идёт УЖЕ В ТУННЕЛЕ, то есть прокси до нас дошёл. Подменой
                    // сертификата это зовём, только когда сертификат и отвергнут: оборванное
                    // соединение выглядит так же, и назвать его подменой значило бы отправить
                    // администратора ставить корневой сертификат от несуществующей беды.
                    return Chain(ex).Any(inner => inner is AuthenticationException)
                        ? OutboundProblem.ProxyTlsIntercepted
                        : OutboundProblem.None;
                if (http.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
                    return OutboundProblem.ProxyUnreachable;
            }

            // Рукопожатие TLS не состоялось, и сертификат отвергнут. При прокси это почти всегда
            // его проверка трафика: сертификат цели подменён своим, а корпоративный корень в
            // хранилище доверенных не положен. Почта приходит сюда своим типом (MailKit).
            if (e is AuthenticationException || e.GetType().Name == "SslHandshakeException")
                return OutboundProblem.ProxyTlsIntercepted;

            // Почта (MailKit) ходит не HTTP-клиентом и говорит о прокси по-своему.
            if (e.GetType().Name == "ProxyProtocolException")
                return FromText(e.Message);

            if (e is SocketException) return OutboundProblem.ProxyUnreachable;
        }

        return FromText(ChainText(ex));
    }

    /// <summary>Фраза для человека. Адрес прокси называем: без него совет «проверьте прокси» некуда приложить.</summary>
    public static string Explain(OutboundProblem problem, OutboundService service, OutboundProxyState state)
    {
        var proxy = Mask((state.ProxyFor(service) ?? state.Configured)?.ToString() ?? "прокси");
        var name = OutboundProxy.DisplayName(service);
        return problem switch
        {
            OutboundProblem.ProxyUnreachable =>
                $"Прокси {proxy} не отвечает: до самого прокси соединение не дошло. Сервис «{name}» здесь ни при чём — проверьте адрес прокси и его доступность из системы.",
            OutboundProblem.ProxyAuth =>
                $"Прокси {proxy} требует вход или не принял логин с паролем. Укажите их в разделе «Прокси» настроек.",
            OutboundProblem.ProxyForbidden =>
                $"Прокси {proxy} запретил обращение к сервису «{name}». Разрешить этот адрес может только тот, кто настраивает прокси.",
            OutboundProblem.ProxyUpstream =>
                $"Прокси {proxy} принял запрос, но сам не достучался до сервиса «{name}». Это отказ на стороне прокси или сети за ним.",
            OutboundProblem.ProxyTlsIntercepted =>
                $"Прокси {proxy} подменяет сертификат сервиса «{name}» (проверка TLS). Чтобы система ему доверяла, корневой сертификат прокси должен лежать в хранилище доверенных на сервере.",
            OutboundProblem.ProxyOffForService =>
                $"Напрямую подключиться не удалось, а прокси {proxy} в системе настроен — у сервиса «{name}» просто снята галка «Через прокси».",
            _ => "",
        };
    }

    /// <summary>
    /// Ответ получен — разбираем его код. Отдельно от исключений потому, что по незашифрованному
    /// HTTP прокси отвечает не отказом соединения, а обычным ответом, и тот доезжает до вызывающего.
    /// </summary>
    public static OutboundProblem DiagnoseStatus(int status, OutboundService service, OutboundProxyState state)
    {
        if (state.ProxyFor(service) is null) return OutboundProblem.None;
        return status switch
        {
            // 407 придумали для прокси, и сервис за туннелем так не отвечает.
            407 => OutboundProblem.ProxyAuth,
            _ => OutboundProblem.None,
        };
    }

    /// <summary>Код ответа прокси на CONNECT → диагноз. Здесь кодам верим: отвечал ими прокси, а не сервис.</summary>
    private static OutboundProblem FromTunnel(HttpRequestException ex)
    {
        var text = ChainText(ex);
        var matches = TunnelStatus.Matches(text);
        if (matches.Count == 0)
        {
            // Туннель не построен, а кода нет — так отвечает SOCKS: он сообщает о себе словами, и
            // это единственное место, где приходится смотреть на текст. Не узнали слов — считаем,
            // что соединения с прокси не вышло: туннель не построен в любом случае.
            var byText = FromText(text);
            return byText == OutboundProblem.None ? OutboundProblem.ProxyUnreachable : byText;
        }

        return int.Parse(matches[^1].Groups[1].Value) switch
        {
            407 => OutboundProblem.ProxyAuth,
            403 or 405 => OutboundProblem.ProxyForbidden,
            502 or 503 or 504 => OutboundProblem.ProxyUpstream,
            _ => OutboundProblem.ProxyForbidden,
        };
    }

    /// <summary>
    /// Разбор по словам — только там, где кода нет: SOCKS и MailKit сообщают о себе текстом. Это
    /// последнее средство, а не основной путь: слова меняются от версии к версии, коды — нет.
    /// </summary>
    private static OutboundProblem FromText(string message)
    {
        if (message.Contains("authenticat", StringComparison.OrdinalIgnoreCase)
            || message.Contains("407", StringComparison.Ordinal))
            return OutboundProblem.ProxyAuth;
        if (message.Contains("not allowed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
            || message.Contains("403", StringComparison.Ordinal))
            return OutboundProblem.ProxyForbidden;
        return OutboundProblem.None;
    }

    /// <summary>Отказ именно СОЕДИНЕНИЯ, а не ответа: только он что-то говорит о пути наружу.</summary>
    private static bool IsConnectFailure(Exception ex) => Chain(ex).Any(e =>
        e is SocketException
        || (e is HttpRequestException h && h.HttpRequestError
            is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError
            or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError));

    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException) yield return e;
    }

    /// <summary>Тексты всей цепочки: подробности отказа лежат во вложенном, а не в верхнем сообщении.</summary>
    private static string ChainText(Exception ex) => string.Join(" ", Chain(ex).Select(e => e.Message));
}
