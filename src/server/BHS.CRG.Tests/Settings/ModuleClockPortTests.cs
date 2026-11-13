using System.Globalization;
using System.Linq.Expressions;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Documents;

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
            new At(DateTimeOffset.Parse(utcNow, CultureInfo.InvariantCulture)), new Sites());

        Assert.Equal(DateOnly.ParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture), await port.TodayAsync());
    }

    /// <summary>
    /// «Сегодня на стройке» — по поясу стройки (ТЗ CORE-5, WORK-8; work-11-assignments): в 20:00 по Москве
    /// во Владивостоке уже завтра, а «сегодня компании» остаётся прежним. Граница суток проверяется
    /// подстановкой часов на 23:59:59 и 00:00:00 по поясу стройки — без ожидания полуночи.
    /// </summary>
    [Theory]
    [InlineData("2026-10-31T17:00:00Z", "2026-11-01", "2026-10-31")] // 20:00 по Москве, 03:00 во Владивостоке
    [InlineData("2026-10-31T13:59:59Z", "2026-10-31", "2026-10-31")] // 23:59:59 во Владивостоке
    [InlineData("2026-10-31T14:00:00Z", "2026-11-01", "2026-10-31")] // 00:00:00 во Владивостоке
    public async Task Сегодня_на_стройке_по_её_поясу_а_сегодня_компании_отдельно(
        string utcNow, string onSite, string company)
    {
        var vladivostok = Construction.Create("Владивосток, порт", Guid.NewGuid());
        vladivostok.SetTimeZone("Asia/Vladivostok");
        var port = new ModuleClockPort(
            new CompanyZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow")),
            new At(DateTimeOffset.Parse(utcNow, CultureInfo.InvariantCulture)), new Sites(vladivostok));

        Assert.Equal(Day(onSite), await port.TodayOnSiteAsync(vladivostok.Id));
        Assert.Equal(Day(company), await port.TodayAsync());
    }

    /// <summary>Пояс не задан у стройки — сутки компании; мгновение переводится в дату стройки тем же правилом.</summary>
    [Fact]
    public async Task Стройка_без_пояса_живёт_по_компании_а_мгновение_даёт_дату_стройки()
    {
        var moscow = Construction.Create("Склад, Москва", Guid.NewGuid());
        var vladivostok = Construction.Create("Владивосток, порт", Guid.NewGuid());
        vladivostok.SetTimeZone("Asia/Vladivostok");
        var port = new ModuleClockPort(
            new CompanyZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow")),
            new At(DateTimeOffset.Parse("2026-10-31T20:59:59Z", CultureInfo.InvariantCulture)), new Sites(moscow, vladivostok));

        Assert.Equal(Day("2026-10-31"), await port.TodayOnSiteAsync(moscow.Id));
        var instant = DateTimeOffset.Parse("2026-10-31T21:00:00Z", CultureInfo.InvariantCulture);
        Assert.Equal(Day("2026-11-01"), await port.SiteDateOfAsync(moscow.Id, instant));
        Assert.Equal(Day("2026-11-01"), await port.SiteDateOfAsync(vladivostok.Id, instant));
    }

    /// <summary>Удалённая стройка — «стройки нет», а не дата по поясу компании.</summary>
    [Fact]
    public async Task Удалённая_стройка_даёт_null_а_не_дату_компании()
    {
        var port = new ModuleClockPort(
            new CompanyZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow")),
            new At(DateTimeOffset.Parse("2026-10-31T12:00:00Z", CultureInfo.InvariantCulture)), new Sites());

        Assert.Null(await port.TodayOnSiteAsync(Guid.NewGuid()));
        Assert.Null(await port.SiteDateOfAsync(Guid.NewGuid(), DateTimeOffset.UnixEpoch));
    }

    private static DateOnly Day(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Стройки в памяти: порту часов нужно одно чтение по идентификатору.</summary>
    private sealed class Sites(params Construction[] all) : IRepository<Construction>
    {
        public Task<Construction?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(all.FirstOrDefault(c => c.Id == id));

        public Task<IReadOnlyList<Construction>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Construction>>(all);

        public Task<IReadOnlyList<Construction>> FindAsync(
            Expression<Func<Construction, bool>> predicate, CancellationToken ct = default) => throw new NotSupportedException();

        public Task AddAsync(Construction entity, CancellationToken ct = default) => throw new NotSupportedException();
        public void Update(Construction entity) => throw new NotSupportedException();
        public void Remove(Construction entity) => throw new NotSupportedException();
        public Task SaveChangesAsync(CancellationToken ct = default) => throw new NotSupportedException();
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
