namespace BHS.CRG.Application.Settings;

/// <summary>
/// Что система хранит за КОМПАНИЕЙ (ТЗ CORE-25.3, issue #960).
///
/// Каталог стоит вместо проверки, как у <see cref="UserSettingKeys" />: адрес принимает только
/// объявленный ключ. Опечатка иначе сохранилась бы успешно и молча, а настройка «не приезжала» бы —
/// отказ, переодетый в удавшуюся запись.
///
/// ⚠️ Объявлено ровно то, у чего есть потребитель. Пояс компании читает стройка, у которой своего
/// пояса нет; остальные настройки экземпляра приедут вместе со своими работами.
/// </summary>
public static class AppSettingKeys
{
    /// <summary>
    /// Часовой пояс компании в форме IANA (<c>Europe/Moscow</c>) — умолчание для строек, у которых
    /// пояс не выбран (ТЗ CORE-5, WORK-8).
    ///
    /// <para>⚠️ Значение проверяется ЧЕРЕЗ <see cref="TimeZoneInfo" />, а не регулярным выражением:
    /// список поясов знает система, он меняется, и «похоже на пояс» не означает «такой пояс есть».
    /// Пояс, который не разбирается, превратился бы в отказ при первом же подсчёте суток — то есть
    /// далеко от места, где его ввели.</para>
    /// </summary>
    public const string CompanyTimeZone = "company.timezone";

    /// <summary>Предел длины значения: идентификаторы поясов короткие, и снаружи приходит что угодно.</summary>
    public const int MaxValueLength = 128;

    /// <summary>Объявлен ли ключ. Незнакомый — отказ, а не молчаливая запись.</summary>
    public static bool IsKnown(string key) => key == CompanyTimeZone;

    /// <summary>
    /// Разбирается ли идентификатор пояса этой системой. Пустая строка — не пояс: «как у компании»
    /// у самой компании не значит ничего.
    /// </summary>
    public static bool IsKnownTimeZone(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId) || ianaId.Length > MaxValueLength) return false;
        return TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out _);
    }
}
