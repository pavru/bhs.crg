using BHS.CRG.Application.Common;
using BHS.CRG.Application.Settings;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// «Сегодня» для модулей — в поясе компании (ТЗ CORE-5; задача G1c, issue #1090) и на стройке, по её
/// действующему поясу (ТЗ WORK-8; change <c>work-11-assignments</c>).
///
/// <para>Момент берётся у <see cref="TimeProvider" />, а не у <c>DateTime.Now</c>: иначе границу суток
/// нельзя было бы проверить тестом — только дождаться полуночи. Пояс — у настройки ядра; не задан —
/// пояс сервера (см. <see cref="IAppSettingsStore.GetCompanyTimeZoneAsync" />).</para>
///
/// <para>Пояс читается один раз на область (порт — scoped, то есть на запрос): запись счёта и ответ чтения
/// следом спрашивают «сегодня» оба, и второй запрос к настройкам ничего бы не добавил (ревью PR #1790).
/// Стройка — тоже один раз на область: «мои стройки» спрашивают её дважды — дата и граница.</para>
/// </summary>
public sealed class ModuleClockPort(
    IAppSettingsStore settings, TimeProvider time, IRepository<Construction> constructions) : IModuleClock
{
    private TimeZoneInfo? _zone;
    private readonly Dictionary<Guid, TimeZoneInfo?> _sites = [];

    public async Task<DateOnly> TodayAsync(CancellationToken ct = default) =>
        SiteZones.DateOf(await CompanyAsync(ct), time.GetUtcNow());

    public Task<DateOnly?> TodayOnSiteAsync(Guid constructionId, CancellationToken ct = default) =>
        SiteDateOfAsync(constructionId, time.GetUtcNow(), ct);

    public async Task<DateOnly?> SiteDateOfAsync(Guid constructionId, DateTimeOffset instant, CancellationToken ct = default) =>
        await SiteAsync(constructionId, ct) is { } zone ? SiteZones.DateOf(zone, instant) : null;

    private async Task<TimeZoneInfo> CompanyAsync(CancellationToken ct) =>
        _zone ??= await settings.GetCompanyTimeZoneAsync(ct);

    /// <summary>Пояс стройки; <c>null</c> — стройки нет (удалена или не было).</summary>
    private async Task<TimeZoneInfo?> SiteAsync(Guid constructionId, CancellationToken ct)
    {
        if (_sites.TryGetValue(constructionId, out var known)) return known;
        var site = await constructions.GetByIdAsync(constructionId, ct);
        var zone = site is null ? null : SiteZones.Of(site.TimeZoneId, await CompanyAsync(ct)).Zone;
        _sites[constructionId] = zone;
        return zone;
    }
}
