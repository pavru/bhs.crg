using System.Net;
using System.Net.Sockets;
using BHS.CRG.Infrastructure.Http;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Как открывается соединение к имени с несколькими адресами. Проверяется без сети: попытка
/// приходит делегатом, и единственный тест с настоящими сокетами живёт на петле.
///
/// Смысл всего этого — в том, что платформа перебирает адреса по очереди, и один недостижимый
/// съедает весь бюджет соединения (issue #917). Ошибка здесь не видна ни на каком экране: она
/// выглядит как «иногда медленно».
/// </summary>
public class OutboundConnectTests
{
    private static readonly IPAddress V6A = IPAddress.Parse("2001:db8::1");
    private static readonly IPAddress V6B = IPAddress.Parse("2001:db8::2");
    private static readonly IPAddress V6C = IPAddress.Parse("2001:db8::3");
    private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress V4B = IPAddress.Parse("192.0.2.2");

    private static Socket Fake() => new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    [Fact]
    public void Семейства_чередуются_а_порядок_внутри_семейства_сохраняется()
    {
        // Порядок внутри семейства задала система по RFC 6724 — пересортировывать его мы не вправе.
        var mixed = OutboundConnect.Interleave([V6A, V6B, V6C, V4A, V4B], v6Supported: true, v4Supported: true);

        Assert.Equal([V6A, V4A, V6B, V4B, V6C], mixed);
    }

    [Fact]
    public void Первым_идёт_семейство_которое_система_поставила_первым()
    {
        var mixed = OutboundConnect.Interleave([V4A, V6A, V4B], v6Supported: true, v4Supported: true);

        Assert.Equal([V4A, V6A, V4B], mixed);
    }

    [Fact]
    public void Неподдерживаемое_семейство_отбрасывается_до_гонки()
    {
        // Сеть Docker по умолчанию без IPv6: создание сокета AF_INET6 бросило бы исключение раньше
        // всякой гонки, поэтому такие кандидаты не должны доходить до попытки вовсе.
        var mixed = OutboundConnect.Interleave([V6A, V4A, V6B], v6Supported: false, v4Supported: true);

        Assert.Equal([V4A], mixed);
    }

    [Fact]
    public async Task Гонка_не_ждёт_зависшего_кандидата()
    {
        var started = new List<IPAddress>();
        var winner = await OutboundConnect.RaceAsync([V6A, V4A], 443, async (address, _, ct) =>
        {
            lock (started) started.Add(address);
            // Первый адрес — недостижимый: SYN уходит в никуда, отказ придёт через десятки секунд.
            if (address.Equals(V6A)) { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            await Task.Delay(10, ct);
            return Fake();
        }, TimeSpan.FromMilliseconds(30), CancellationToken.None);

        using (winner) Assert.False(winner.SafeHandle.IsClosed);

        // Второй кандидат запущен, хотя первый не ответил и не отказал: фора — интервал
        // чередования, а не таймаут, и ждать отказа первого мы не обязаны.
        Assert.Equal([V6A, V4A], started);
    }

    [Fact]
    public async Task Проигравший_сокет_закрывается()
    {
        Socket? loser = null;
        var winner = await OutboundConnect.RaceAsync([V6A, V4A], 443, async (address, _, ct) =>
        {
            if (address.Equals(V6A))
            {
                // Успевает подключиться уже после чужой победы — и обязан быть закрыт, иначе
                // соединение останется висеть у той стороны.
                await Task.Delay(120, CancellationToken.None);
                return loser = Fake();
            }
            await Task.Delay(10, ct);
            return Fake();
        }, TimeSpan.FromMilliseconds(20), CancellationToken.None);

        winner.Dispose();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && (loser is null || !loser.SafeHandle.IsClosed))
            await Task.Delay(25);

        Assert.NotNull(loser);
        Assert.True(loser!.SafeHandle.IsClosed, "проигравший сокет остался открытым");
    }

    [Fact]
    public async Task Когда_не_ответил_никто_отказ_называет_адреса()
    {
        var failure = await Assert.ThrowsAsync<SocketException>(() =>
            OutboundConnect.RaceAsync([V6A, V4A], 443, (_, _, _) =>
                Task.FromException<Socket>(new SocketException((int)SocketError.NetworkUnreachable)),
                TimeSpan.FromMilliseconds(10), CancellationToken.None));

        Assert.Contains("2001:db8::1", failure.Message);
        Assert.Contains("192.0.2.1", failure.Message);
    }

    [Fact]
    public async Task Отмена_вызывающего_прекращает_гонку()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OutboundConnect.RaceAsync([V6A, V4A], 443,
                (_, _, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct).ContinueWith(_ => Fake(), ct),
                TimeSpan.FromMilliseconds(20), cts.Token));
    }

    [Fact]
    public async Task На_петле_проходит_весь_путь_целиком()
    {
        // Единственный тест с настоящими сокетами: внешней сети и DNS не требует, но проходит
        // разрешение имени, отбор кандидатов и подключение — то есть то, что делегат не покрывает.
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(4);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        _ = listener.AcceptAsync();

        var endPoint = new DnsEndPoint(IPAddress.Loopback.ToString(), port);
        using var socket = await OutboundConnect.ConnectAsync(endPoint, filter: null, CancellationToken.None);

        Assert.True(socket.Connected);
    }

    [Fact]
    public async Task Проверка_адреса_переживает_настройку_по_умолчанию_в_любом_порядке()
    {
        // Сторож регрессии безопасности. Настройка по умолчанию и настройка защищённого клиента
        // применяются к одному обработчику, и до issue #917 порядок регистрации в DI решал, кто
        // кого затрёт. Затираемой стороной была проверка адреса — а не видно этого ни на одном
        // экране: «скачай мне файл» просто начал бы доставать соседей по сети.
        foreach (var guardFirst in new[] { true, false })
        {
            using var handler = new SocketsHttpHandler();
            if (guardFirst) { OutboundAddressPolicy.ApplyGuard(handler); OutboundConnect.Apply(handler); }
            else { OutboundConnect.Apply(handler); OutboundAddressPolicy.ApplyGuard(handler); }

            using var client = new HttpClient(handler);
            var failure = await Assert.ThrowsAnyAsync<Exception>(
                () => client.GetAsync("http://127.0.0.1:1/"));

            var refusal = failure as OutboundAddressRefusedException ?? failure.InnerException as OutboundAddressRefusedException;
            Assert.True(refusal is not null,
                $"порядок guardFirst={guardFirst}: проверка адреса не сработала, получили {failure.GetType().Name}: {failure.Message}");
        }
    }
}
