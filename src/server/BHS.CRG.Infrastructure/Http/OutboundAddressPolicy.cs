using System.Net;
using System.Net.Sockets;

namespace BHS.CRG.Infrastructure.Http;

/// <summary>
/// Куда приложению разрешено ходить по ссылке, пришедшей ИЗВНЕ — от пользователя или из выдачи
/// поиска. Единственная точка правил: клиентов несколько, и каждый со своей проверкой разошёлся бы
/// с остальными.
///
/// Правило одно: снаружи ссылка ведёт в интернет, а не внутрь периметра. Приложение живёт рядом с
/// базой, хранилищем и локальными службами, их адреса известны из файлов развёртывания, а ответ по
/// ссылке возвращается пользователю — то есть без проверки «скачай мне файл» превращается в чтение
/// соседей по сети.
///
/// Проверяется РЕЗУЛЬТАТ РАЗРЕШЕНИЯ ИМЕНИ, а не сама строка: имя в публичном домене может указывать
/// куда угодно, и запрет по тексту адреса ничего не значит. Если имя разрешается в несколько
/// адресов, годными должны быть ВСЕ — иначе достаточно одной подходящей записи в DNS.
///
/// ⚠️ Не применяется к адресам СЛУЖБ, заданным администратором (адрес Ollama и т.п.): они
/// внутренние по замыслу, и это разные задачи. См. отчёт, раздел 3.
/// </summary>
public static class OutboundAddressPolicy
{
    /// <summary>Сколько перенаправлений проходим сами. .NET по умолчанию идёт до 50 и каждое
    /// следующее уже не наша проверка — поэтому переходы считаем здесь и заново проверяем цель.</summary>
    public const int MaxRedirects = 5;

    /// <summary>
    /// Текст отказа ОДИН на все причины. Разные тексты («хост не ответил», «403», «не тот тип»)
    /// отвечают на вопрос «что там внутри?» — по ним внутреннюю сеть можно нарисовать, ничего не
    /// скачав. Подробности уходят в лог, пользователю достаётся один ответ.
    /// </summary>
    public const string RefusalMessage =
        "Не удалось загрузить файл по этой ссылке. Проверьте адрес — он должен вести на общедоступный ресурс.";

    /// <summary>Ссылка разобрана и это http(s). Иначе — тот же общий отказ.</summary>
    public static Uri RequireHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : throw new OutboundAddressRefusedException($"схема или форма адреса не годятся: {url}");

    /// <summary>Разрешает имя и требует, чтобы КАЖДЫЙ полученный адрес был публичным.</summary>
    public static async Task EnsureAllowedAsync(Uri uri, CancellationToken ct = default)
    {
        // Пустое имя — отказ: разрешение пустой строки возвращает адреса САМОЙ МАШИНЫ, то есть
        // решение зависело бы от того, как настроены её сетевые интерфейсы, а не от ссылки.
        if (string.IsNullOrWhiteSpace(uri.Host))
            throw new OutboundAddressRefusedException($"в адресе нет имени хоста: {uri}");

        IPAddress[] addresses;
        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try { addresses = await Dns.GetHostAddressesAsync(uri.Host, ct); }
            // Неразрешимое имя — тоже отказ, и с тем же текстом: иначе «не резолвится» против
            // «резолвится, но запрещено» само по себе отвечает на вопрос об именах внутри сети.
            catch (Exception e) when (e is SocketException or ArgumentException)
            {
                throw new OutboundAddressRefusedException($"имя не разрешается: {uri.Host}");
            }
        }

        if (addresses.Length == 0)
            throw new OutboundAddressRefusedException($"имя не разрешается: {uri.Host}");

        foreach (var address in addresses)
            if (IsBlocked(address))
                throw new OutboundAddressRefusedException($"адрес вне общедоступных: {uri.Host} → {address}");
    }

    /// <summary>
    /// Адрес принадлежит к диапазону, наружу не ведущему. Чистая функция — на ней и держится
    /// проверка, поэтому она покрыта тестами по диапазонам.
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        // IPv4, записанный формой IPv6, проверяем как IPv4. Форм таких НЕСКОЛЬКО, и .NET узнаёт
        // только одну (`::ffff:a.b.c.d`) — остальные для него обычные глобальные адреса, то есть
        // достаточно записать цель иначе, чтобы правила не сработали:
        //
        //   ::a.b.c.d          (0000…0000 + IPv4)      — IPv4-совместимый
        //   ::ffff:0:a.b.c.d   (…ffff 0000 + IPv4)     — IPv4-транслированный
        //   64:ff9b::a.b.c.d   (NAT64)                 — в сетях только-IPv6 доходит до цели
        //   2002:AABB:CCDD::   (6to4, IPv4 в байтах 2-5)
        if (ExtractEmbeddedIPv4(address) is { } embedded) address = embedded;

        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                0 => true,                                   // 0.0.0.0/8 — «этот хост»
                10 => true,                                  // 10.0.0.0/8
                127 => true,                                 // на случай, если IsLoopback не сработал
                169 when b[1] == 254 => true,                // 169.254.0.0/16 — link-local, там же метаданные облаков
                172 when b[1] >= 16 && b[1] <= 31 => true,    // 172.16.0.0/12
                192 when b[1] == 168 => true,                // 192.168.0.0/16
                100 when b[1] >= 64 && b[1] <= 127 => true,   // 100.64.0.0/10 — CGNAT
                >= 224 => true,                              // multicast и зарезервированное, включая 255.255.255.255
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;          // fc00::/7 — уникальные локальные
            if (address.Equals(IPAddress.IPv6Any)) return true;
            return false;
        }

        // Неизвестное семейство адресов наружу не ведёт — отказываем.
        return true;
    }

    /// <summary>
    /// Вынимает IPv4, спрятанный в записи IPv6, — чтобы дальше он проверялся обычными правилами.
    /// null, если адрес не IPv6 или ничего не прячет.
    /// </summary>
    private static IPAddress? ExtractEmbeddedIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return null;
        var b = address.GetAddressBytes();

        // 2002::/16 — 6to4: адрес IPv4 стоит вторым-пятым байтом.
        if (b[0] == 0x20 && b[1] == 0x02) return new IPAddress(b[2..6]);

        // 64:ff9b::/96 — NAT64.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B
            && b[4..12].All(x => x == 0)) return new IPAddress(b[12..16]);

        // Дальше — формы с двенадцатью ведущими байтами, отличающиеся только серединой:
        // ::a.b.c.d (всё нулями), ::ffff:a.b.c.d (ffff в 10-11), ::ffff:0:a.b.c.d (ffff в 8-9).
        if (!b[0..8].All(x => x == 0)) return null;
        var isCompatible = b[8..12].All(x => x == 0);
        var isMapped = b[8] == 0 && b[9] == 0 && b[10] == 0xFF && b[11] == 0xFF;
        var isTranslated = b[8] == 0xFF && b[9] == 0xFF && b[10] == 0 && b[11] == 0;
        if (!isCompatible && !isMapped && !isTranslated) return null;

        // «::» и «::1» — не спрятанный IPv4, их разбирают обычные правила ниже по тексту.
        var tail = b[12..16];
        if (tail[0] == 0 && tail[1] == 0 && tail[2] == 0 && tail[3] <= 1) return null;
        return new IPAddress(tail);
    }

    /// <summary>
    /// Обработчик, который проверяет адрес В МОМЕНТ ПОДКЛЮЧЕНИЯ и подключается именно к
    /// проверенному.
    ///
    /// Без этого проверка имени не значит ничего: мы разрешаем имя сами, а <c>HttpClient</c> при
    /// открытии соединения разрешает его ЗАНОВО — и владелец имени вправе ответить во второй раз
    /// иначе. Первый ответ проходит проверку, по второму открывается соединение. Разрыв между
    /// проверкой и подключением закрывается только здесь: адреса берём один раз и подключаемся к
    /// ним же.
    /// </summary>
    public static SocketsHttpHandler CreateGuardedHandler() => new()
    {
        // Перенаправления проходит SafeHttpGet, проверяя цель каждого.
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            IPAddress[] resolved;
            if (IPAddress.TryParse(host, out var literal)) resolved = [literal];
            else
            {
                try { resolved = await Dns.GetHostAddressesAsync(host, ct); }
                catch (Exception e) when (e is SocketException or ArgumentException)
                {
                    throw new OutboundAddressRefusedException($"имя не разрешается: {host}");
                }
            }

            if (resolved.Length == 0)
                throw new OutboundAddressRefusedException($"имя не разрешается: {host}");
            foreach (var address in resolved)
                if (IsBlocked(address))
                    throw new OutboundAddressRefusedException($"адрес вне общедоступных: {host} → {address}");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Подключаемся к ПРОВЕРЕННЫМ адресам, а не к имени: иначе внутри снова случилось бы
                // разрешение имени, и всё вышесказанное потеряло бы смысл.
                await socket.ConnectAsync(resolved, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

/// <summary>
/// Отказ по адресу назначения. Сообщение внутри — ДЛЯ ЛОГА: причина названа точно, чтобы разбирать
/// обращения. Пользователю показывают <see cref="OutboundAddressPolicy.RefusalMessage"/>, один на
/// все случаи.
/// </summary>
public class OutboundAddressRefusedException(string reason)
    : Exception($"Исходящий запрос отклонён: {reason}");
