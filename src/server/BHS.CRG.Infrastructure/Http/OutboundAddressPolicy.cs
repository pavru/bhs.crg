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
        // Адрес IPv4, записанный как IPv6 (::ffff:127.0.0.1), проверяем как IPv4: иначе запись
        // формы достаточно, чтобы обойти правила для «настоящего» IPv4.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

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
}

/// <summary>
/// Отказ по адресу назначения. Сообщение внутри — ДЛЯ ЛОГА: причина названа точно, чтобы разбирать
/// обращения. Пользователю показывают <see cref="OutboundAddressPolicy.RefusalMessage"/>, один на
/// все случаи.
/// </summary>
public class OutboundAddressRefusedException(string reason)
    : Exception($"Исходящий запрос отклонён: {reason}");
