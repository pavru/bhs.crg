using System.Net;
using BHS.CRG.Infrastructure.Http;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Правила для адресов, куда приложение ходит по ссылке извне. Проверка держится на разборе
/// диапазонов, поэтому диапазоны и проверяются поимённо: ошибка здесь не видна ни на каком экране —
/// она видна только тем, кто её ищет.
/// </summary>
public class OutboundAddressPolicyTests
{
    [Theory]
    // Петля и «этот хост»
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    // Частные диапазоны
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    // Link-local: там же адрес метаданных облачных площадок
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    // Диапазон оператора связи между абонентом и сетью (CGNAT)
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    // Групповая рассылка и широковещание
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    [InlineData("255.255.255.255")]
    // IPv6
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::")]
    // Запись IPv4 внутри IPv6: формой записи правила не обходятся
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void NonPublicAddress_Blocked(string ip)
        => Assert.True(OutboundAddressPolicy.IsBlocked(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.15.255.255")]  // соседний с частным диапазоном снизу
    [InlineData("172.32.0.1")]      // и сверху
    [InlineData("100.63.255.255")]  // соседний с CGNAT снизу
    [InlineData("100.128.0.1")]     // и сверху
    [InlineData("169.253.0.1")]     // соседний с link-local
    [InlineData("11.0.0.1")]
    [InlineData("223.255.255.255")] // последний перед групповой рассылкой
    [InlineData("2606:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    public void PublicAddress_Allowed(string ip)
        => Assert.False(OutboundAddressPolicy.IsBlocked(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("https://example.com/file.pdf")]
    [InlineData("http://example.com/file.pdf")]
    public void HttpUrl_Accepted(string url)
        => Assert.Equal(url, OutboundAddressPolicy.RequireHttpUrl(url).ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не ссылка")]
    [InlineData("/только/путь")]
    [InlineData("file:///c:/windows/win.ini")]
    [InlineData("ftp://example.com/file.pdf")]
    [InlineData("gopher://example.com/")]
    public void NonHttpUrl_Refused(string? url)
        => Assert.Throws<OutboundAddressRefusedException>(() => OutboundAddressPolicy.RequireHttpUrl(url));

    [Theory]
    [InlineData("http://127.0.0.1:9000/bucket/object")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]:5000/api/users")]
    [InlineData("http://10.0.0.5/internal")]
    public async Task LiteralNonPublicHost_Refused(string url)
    {
        var uri = OutboundAddressPolicy.RequireHttpUrl(url);
        await Assert.ThrowsAsync<OutboundAddressRefusedException>(
            () => OutboundAddressPolicy.EnsureAllowedAsync(uri));
    }

    /// <summary>
    /// Имя, которое не разрешается, — тоже отказ. Иначе «не резолвится» против «резолвится, но
    /// запрещено» само по себе отвечало бы на вопрос, какие имена внутри сети существуют.
    /// </summary>
    [Fact]
    public async Task UnresolvableHost_Refused()
    {
        var uri = OutboundAddressPolicy.RequireHttpUrl(
            "http://this-name-does-not-exist-8b31d0c47f2a.invalid/file.pdf");
        await Assert.ThrowsAsync<OutboundAddressRefusedException>(
            () => OutboundAddressPolicy.EnsureAllowedAsync(uri));
    }

    /// <summary>
    /// Текст, который видит пользователь, — КОНСТАНТА: он не собирается из причины и потому одинаков
    /// для запрещённого адреса, неразрешимого имени и молчания хоста. Различие этих текстов и есть
    /// то, по чему внутреннюю сеть можно нарисовать, ничего не скачав.
    ///
    /// Сказать «проверьте адрес» при этом не возбраняется — речь о ссылке, которую пользователь ввёл
    /// сам; запрещено называть, ЧТО именно с ней не так.
    /// </summary>
    [Fact]
    public void RefusalMessage_IsOneConstantWithoutDetails()
    {
        var m = OutboundAddressPolicy.RefusalMessage;

        // В тексте нет ничего, что зависит от конкретного запроса.
        Assert.DoesNotContain("127.", m);
        Assert.DoesNotContain("169.254", m);
        Assert.DoesNotContain("localhost", m, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{", m);   // не шаблон с подстановкой
        Assert.DoesNotContain("404", m);
        Assert.DoesNotContain("timeout", m, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Причина остаётся ВНУТРИ исключения — для лога и разбора обращений. Наружу её выносит
    /// вызывающий, подставляя общий текст; здесь фиксируем, что причина вообще есть и она разная.
    /// </summary>
    [Fact]
    public async Task RefusalReason_IsKeptForTheLog()
    {
        var blocked = await Assert.ThrowsAsync<OutboundAddressRefusedException>(
            () => OutboundAddressPolicy.EnsureAllowedAsync(new Uri("http://10.0.0.5/x")));
        var badScheme = Assert.Throws<OutboundAddressRefusedException>(
            () => OutboundAddressPolicy.RequireHttpUrl("ftp://example.com/x"));

        Assert.Contains("10.0.0.5", blocked.Message);
        Assert.NotEqual(blocked.Message, badScheme.Message);
        // И ни одна из причин не равна тому, что покажут пользователю.
        Assert.NotEqual(OutboundAddressPolicy.RefusalMessage, blocked.Message);
    }
}
