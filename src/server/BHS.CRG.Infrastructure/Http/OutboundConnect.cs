using System.Net;
using System.Net.Sockets;

namespace BHS.CRG.Infrastructure.Http;

/// <summary>
/// Как приложение открывает TCP к имени, за которым стоит НЕСКОЛЬКО адресов.
///
/// Платформа перебирает их по очереди («Try each address in turn» —
/// <c>SocketAsyncEventArgs.DnsConnectAsync</c>), и один недостижимый адрес съедает весь бюджет
/// соединения: отказ по SYN приходит через десятки секунд, а <c>ConnectTimeout</c> накрывает не
/// каждый адрес, а всю установку соединения целиком — разрешение имени, весь цикл и рукопожатие
/// TLS. Поэтому коротким сроком это не лечится: он лишь не даёт перебору дойти до живого адреса.
///
/// Здесь адреса пробуются ВНАХЛЁСТ (RFC 8305): семейства чередуются, каждому следующему кандидату
/// даётся фора, побеждает первый ответивший. Happy Eyeballs в <c>SocketsHttpHandler</c> нет — ни
/// выбора семейства, ни параллельных попыток, — поэтому механизм наш.
///
/// ⚠️ Это МЕХАНИКА подключения, а не политика «кому можно звонить». Политика живёт в
/// <see cref="OutboundAddressPolicy"/> и приходит сюда параметром <c>filter</c>. Благодаря этому в
/// кодовой базе ровно одно присваивание <c>ConnectCallback</c>: иначе настройка по умолчанию и
/// проверка адреса вытесняли бы друг друга молча, в зависимости от порядка регистрации в DI.
///
/// Накрывает клиентов из фабрики. НЕ накрывает два места: <c>PluginHost</c> создаёт свой
/// <c>HttpClient</c> мимо фабрики (правится там же отдельно), а хранилище ходит клиентом внутри
/// своего SDK. Названо здесь, чтобы настройка по умолчанию не создавала иллюзию полноты.
/// </summary>
public static class OutboundConnect
{
    /// <summary>
    /// Фора следующему кандидату. Это ИНТЕРВАЛ ЧЕРЕДОВАНИЯ, а не таймаут: попытка, которой дали
    /// фору, не отменяется и продолжает гонку.
    ///
    /// ⚠️ Граница разумного. Желание сделать это значение настраиваемым, увеличить «чтобы было
    /// стабильнее» или добавить повторы с отступом означает, что мы компенсируем неисправную сеть
    /// средствами приложения. На этом месте надо останавливаться и чинить сеть, а не подкручивать
    /// здесь. Выключить гонку целиком (<c>Http:HappyEyeballs=false</c>) — можно: это откат на
    /// штатное поведение платформы, а не подкрутка.
    /// </summary>
    public static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Внешняя граница на всю установку соединения: разрешение имени, все попытки и рукопожатие
    /// TLS. Со включённой гонкой десяти секунд хватает с запасом — при исправном адресе уходит
    /// меньше секунды; срок держится на случай, когда недостижимы ВСЕ адреса сразу.
    /// </summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Ставит обработчику наш способ подключения. <paramref name="filter"/> — крючок политики:
    /// вызывается ОДИН раз, сразу после разрешения имени, и подключаемся мы к тем самым адресам,
    /// которые он вернул. Разрыв между проверкой и подключением закрывается именно этим.
    /// </summary>
    private const string FilterKey = "BHS.CRG.OutboundFilter";

    public static void Apply(SocketsHttpHandler handler, Func<string, IPAddress[], IPAddress[]>? filter = null)
    {
        // Фильтр живёт на обработчике, а не в замыкании, НАРОЧНО. Настройка по умолчанию и настройка
        // защищённого клиента применяются к одному обработчику в порядке регистрации в DI, и раньше
        // этот порядок решал, останется ли проверка адреса вообще: кто применился последним, тот и
        // затирал чужой ConnectCallback. Теперь поздний вызов без фильтра подхватывает уже
        // поставленный, а поздний вызов с фильтром его заменяет — исход одинаков при любом порядке.
        if (filter is not null) handler.Properties[FilterKey] = filter;
        else if (handler.Properties.TryGetValue(FilterKey, out var kept))
            filter = (Func<string, IPAddress[], IPAddress[]>)kept!;

        handler.ConnectTimeout = ConnectTimeout;
        handler.ConnectCallback = async (context, ct) =>
        {
            var socket = await ConnectAsync(context.DnsEndPoint, filter, ct);
            return new NetworkStream(socket, ownsSocket: true);
        };
    }

    /// <summary>Разрешает имя, отдаёт адреса политике и подключается гонкой к тому, что осталось.</summary>
    public static async Task<Socket> ConnectAsync(
        DnsEndPoint endPoint, Func<string, IPAddress[], IPAddress[]>? filter, CancellationToken ct)
    {
        IPAddress[] resolved;
        try { resolved = await ResolveAsync(endPoint.Host, ct); }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            // Неразрешимое имя показываем политике пустым списком, а не исключением: чем считать
            // такой случай — её решение, и отказ у неё один на все причины («не резолвится» против
            // «резолвится, но запрещено» само по себе рассказывает об именах внутри сети).
            if (filter is null) throw;
            resolved = [];
        }

        if (filter is not null) resolved = filter(endPoint.Host, resolved);

        var candidates = Interleave(resolved);
        if (candidates.Count == 0) throw new SocketException((int)SocketError.HostNotFound);

        return await RaceAsync(candidates, endPoint.Port, AttemptAsync, AttemptDelay, ct);
    }

    /// <summary>Литерал не резолвим: для него разрешение имени — лишний поход к DNS и лишний отказ.</summary>
    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
        => IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);

    /// <summary>
    /// Чередует семейства: v6, v4, v6, v4… Порядок ВНУТРИ семейства сохраняется — его задала
    /// система по RFC 6724, и пересортировывать его мы не вправе. Первым идёт то семейство, чей
    /// адрес система поставила первым.
    ///
    /// Семейство, которого нет у машины, отбрасывается здесь же: в сети без IPv6 создание сокета
    /// AF_INET6 бросило бы исключение раньше всякой гонки.
    /// </summary>
    public static IReadOnlyList<IPAddress> Interleave(IEnumerable<IPAddress> addresses)
        => Interleave(addresses, Socket.OSSupportsIPv6, Socket.OSSupportsIPv4);

    /// <summary>
    /// То же, с явной поддержкой семейств. Отдельной перегрузкой — чтобы случай «машина без IPv6»
    /// (сеть Docker по умолчанию) проверялся на любой машине, а не только на такой.
    /// </summary>
    public static IReadOnlyList<IPAddress> Interleave(
        IEnumerable<IPAddress> addresses, bool v6Supported, bool v4Supported)
    {
        List<IPAddress> v6 = [], v4 = [];
        IPAddress? leader = null;
        foreach (var address in addresses)
        {
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && v6Supported) v6.Add(address);
            else if (address.AddressFamily == AddressFamily.InterNetwork && v4Supported) v4.Add(address);
            else continue;
            leader ??= address;
        }

        if (v6.Count == 0) return v4;
        if (v4.Count == 0) return v6;

        var (first, second) = leader!.AddressFamily == AddressFamily.InterNetworkV6 ? (v6, v4) : (v4, v6);
        var mixed = new List<IPAddress>(first.Count + second.Count);
        for (var i = 0; i < Math.Max(first.Count, second.Count); i++)
        {
            if (i < first.Count) mixed.Add(first[i]);
            if (i < second.Count) mixed.Add(second[i]);
        }
        return mixed;
    }

    /// <summary>
    /// Гонка. Попытка приходит делегатом, чтобы это можно было проверить без сети.
    ///
    /// Кандидаты запускаются по одному с интервалом <paramref name="delay"/>; первый успех
    /// побеждает, остальные отменяются. Проигравшие ОБЯЗАНЫ быть закрыты: соединение, которое
    /// успело установиться после чужой победы, иначе останется висеть у той стороны.
    ///
    /// Публичный — ради проверки без сети: это единственное место, где может протечь сокет, и
    /// проверять его догадками по внешнему поведению значит не проверять вовсе.
    /// </summary>
    public static async Task<Socket> RaceAsync(
        IReadOnlyList<IPAddress> candidates, int port,
        Func<IPAddress, int, CancellationToken, Task<Socket>> attempt,
        TimeSpan delay, CancellationToken ct)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        List<Task<Socket>> pending = [];
        List<Exception> errors = [];
        Socket? winner = null;
        var next = 0;

        try
        {
            while (winner is null && (next < candidates.Count || pending.Count > 0))
            {
                if (next < candidates.Count) pending.Add(attempt(candidates[next++], port, linked.Token));

                // Пока кандидаты не кончились — ждём либо чьего-то ответа, либо форы. Когда
                // кончились, ждать остаётся только уже запущенные попытки.
                var pause = Task.Delay(next < candidates.Count ? delay : Timeout.InfiniteTimeSpan, linked.Token);
                var completed = await Task.WhenAny([.. pending.Cast<Task>(), pause]);

                if (completed == pause)
                {
                    ct.ThrowIfCancellationRequested();
                    continue;
                }

                var finished = (Task<Socket>)completed;
                pending.Remove(finished);
                if (finished.IsCompletedSuccessfully) winner = finished.Result;
                else errors.Add(finished.Exception?.GetBaseException() ?? new SocketException());
            }
        }
        catch
        {
            Sweep(pending, linked);
            throw;
        }

        Sweep(pending, linked);

        if (winner is not null) return winner;
        ct.ThrowIfCancellationRequested();
        throw new SocketException((int)SocketError.HostUnreachable,
            "Ни один адрес не ответил: " + string.Join(", ", candidates)
            + (errors.Count > 0 ? " — " + string.Join("; ", errors.Select(e => e.Message)) : ""));
    }

    /// <summary>Отменяет проигравших, закрывает то, что успело подключиться, и только потом отпускает источник отмены.</summary>
    private static void Sweep(List<Task<Socket>> pending, CancellationTokenSource linked)
    {
        linked.Cancel();
        if (pending.Count == 0) { linked.Dispose(); return; }
        _ = Task.WhenAll(pending.Select(t => t.ContinueWith(
                x => { if (x.IsCompletedSuccessfully) x.Result.Dispose(); }, TaskScheduler.Default)))
            .ContinueWith(_ => linked.Dispose(), TaskScheduler.Default);
    }

    private static async Task<Socket> AttemptAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
