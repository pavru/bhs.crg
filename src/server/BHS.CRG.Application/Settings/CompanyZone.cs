namespace BHS.CRG.Application.Settings;

/// <summary>
/// Пояс компании из сохранённой строки — одно правило для хранилища настроек и для ответа
/// <c>/api/account/access</c>, который читает настройки одним запросом.
/// </summary>
public static class CompanyZone
{
    /// <summary>
    /// Сохранённый пояс, а если его нет или эта машина его не знает — пояс сервера.
    ///
    /// <para>Сохранённый пояс может не разбираться на ДРУГОЙ машине: база переезжает между системами,
    /// а список поясов у них разный. Тихо подставить серверный — значит посчитать сутки не по
    /// тому поясу и не сказать об этом; поэтому возвращаем серверный, но это видно в настройках.</para>
    /// </summary>
    public static TimeZoneInfo Of(string? stored) =>
        stored is not null && TimeZoneInfo.TryFindSystemTimeZoneById(stored, out var zone) ? zone : TimeZoneInfo.Local;

    /// <summary>
    /// Имя пояса для браузера — IANA («Europe/Moscow»), его понимает <c>Intl</c>. На Windows у
    /// <see cref="TimeZoneInfo" /> имена свои («Russian Standard Time») — их переводим; не переводится —
    /// <c>null</c>, и клиент считает день по своим часам.
    /// </summary>
    public static string? IanaId(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana
        : null;
}
