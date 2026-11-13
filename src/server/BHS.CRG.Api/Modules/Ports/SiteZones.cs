namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Действующий пояс стройки (ТЗ CORE-5, WORK-8; change <c>work-11-assignments</c>): свой, а не задан —
/// пояс компании. Одним местом для двух портов — строек и часов: порт строек называет пояс, часы по нему
/// считают сутки, и разойтись в правиле наследования им нечем.
/// </summary>
internal static class SiteZones
{
    /// <summary>Действующий пояс стройки и признак «взят у компании».</summary>
    /// <param name="own">Пояс, записанный у стройки (<c>Construction.TimeZoneId</c>).</param>
    /// <param name="company">Пояс компании — настройка ядра.</param>
    public static (TimeZoneInfo Zone, bool Inherited) Of(string? own, TimeZoneInfo company)
    {
        // Записанный пояс, который система перестала знать (обновили базу поясов), — не повод ронять
        // чтение: стройка живёт по поясу компании, и экран покажет «как у компании».
        if (!string.IsNullOrWhiteSpace(own) && TimeZoneInfo.TryFindSystemTimeZoneById(own, out var zone))
            return (zone, false);
        return (company, true);
    }

    /// <summary>
    /// Имя пояса в форме IANA. Пояс компании, не заданный настройкой, — пояс сервера, а на Windows его имя
    /// — «Russian Standard Time»: модулю и клиенту обещана форма IANA, и чужая форма разошлась бы со
    /// списком поясов на экране.
    /// </summary>
    public static string IanaId(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana
        : zone.Id;

    /// <summary>Дата в поясе для мгновения.</summary>
    public static DateOnly DateOf(TimeZoneInfo zone, DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
