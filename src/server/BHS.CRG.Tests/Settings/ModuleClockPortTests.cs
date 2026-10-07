using System.Globalization;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.Settings;

namespace BHS.CRG.Tests.Settings;

/// <summary>
/// «Сегодня» для модулей — в поясе компании (ТЗ CORE-5; задача G1c, issue #1090). По нему считаются
/// «осталось дней» и «просрочен» у счёта, и граница суток обязана проходить там, где она проходит у
/// людей компании, а не у сервера.
///
/// <para>Часы подставные: иначе границу суток можно было бы проверить, только дождавшись полуночи.</para>
/// </summary>
public class ModuleClockPortTests
{
    [Theory]
    // Сервер живёт по UTC, компания — в Москве: с 21:00 UTC у неё уже завтра.
    [InlineData("2026-03-10T20:59:59Z", "Europe/Moscow", "2026-03-10")]
    [InlineData("2026-03-10T21:00:00Z", "Europe/Moscow", "2026-03-11")]
    [InlineData("2026-03-10T21:30:00Z", "UTC", "2026-03-10")]
    // И в обе стороны: восточнее — завтра наступает раньше, западнее — сегодня ещё вчера.
    [InlineData("2026-03-10T15:00:00Z", "Asia/Vladivostok", "2026-03-11")]
    [InlineData("2026-03-11T02:00:00Z", "America/New_York", "2026-03-10")]
    public async Task Сегодня_считается_в_поясе_компании_а_не_сервера(string utcNow, string zone, string expected)
    {
        var port = new ModuleClockPort(
            new CompanyZone(TimeZoneInfo.FindSystemTimeZoneById(zone)),
            new At(DateTimeOffset.Parse(utcNow, CultureInfo.InvariantCulture)));

        Assert.Equal(DateOnly.ParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture), await port.TodayAsync());
    }

    private sealed class At(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CompanyZone(TimeZoneInfo zone) : IAppSettingsStore
    {
        public Task<TimeZoneInfo> GetCompanyTimeZoneAsync(CancellationToken ct = default) => Task.FromResult(zone);

        public Task<string?> GetAsync(string key, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> GetManyAsync(
            IReadOnlyCollection<string> keys, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetAsync(string key, string? value, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
