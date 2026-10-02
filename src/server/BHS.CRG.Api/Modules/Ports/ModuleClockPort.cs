using BHS.CRG.Application.Settings;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// «Сегодня» для модулей — в поясе компании (ТЗ CORE-5; задача G1c, issue #1090).
///
/// <para>Момент берётся у <see cref="TimeProvider" />, а не у <c>DateTime.Now</c>: иначе границу суток
/// нельзя было бы проверить тестом — только дождаться полуночи. Пояс — у настройки ядра; не задан —
/// пояс сервера (см. <see cref="IAppSettingsStore.GetCompanyTimeZoneAsync" />).</para>
/// </summary>
public sealed class ModuleClockPort(IAppSettingsStore settings, TimeProvider time) : IModuleClock
{
    public async Task<DateOnly> TodayAsync(CancellationToken ct = default)
    {
        var zone = await settings.GetCompanyTimeZoneAsync(ct);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
    }
}
