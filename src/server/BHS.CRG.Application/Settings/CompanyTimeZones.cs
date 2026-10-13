namespace BHS.CRG.Application.Settings;

/// <summary>Пояс в списке выбора: идентификатор IANA и название городом, как его называют в России.</summary>
public sealed record OfferedTimeZone(string Id, string City);

/// <summary>
/// Пояса, которые экран настройки предлагает на выбор (issue #1148).
///
/// <para>Список — подсказка экрана, а не запрет: <c>PUT /api/settings/company</c> принимает любой
/// пояс, известный этой машине (<see cref="AppSettingKeys.IsKnownTimeZone" />), и выбранный вне списка
/// экран показывает как есть. Закрытым, как у валюты учёта, список не сделан нарочно: пояс ничего не
/// пересчитывает и меняется обратно, а полный перечень системы — четыре сотни строк с синонимами, в
/// котором «Москва» ищут под буквой E.</para>
///
/// <para>Состав — пояса России по одному на смещение и столицы соседей, чьи валюты есть в
/// <see cref="AccountingCurrencies" />. Отдаёт его сервер, а не клиент: предложить пояс, которого
/// на этой машине нет, значило бы предложить отказ.</para>
/// </summary>
public static class CompanyTimeZones
{
    private static readonly OfferedTimeZone[] All =
    [
        new("Europe/Kaliningrad", "Калининград"),
        new("Europe/Minsk", "Минск"),
        new("Europe/Moscow", "Москва"),
        new("Europe/Samara", "Самара"),
        new("Asia/Yekaterinburg", "Екатеринбург"),
        new("Asia/Almaty", "Алматы"),
        new("Asia/Omsk", "Омск"),
        new("Asia/Krasnoyarsk", "Красноярск"),
        new("Asia/Irkutsk", "Иркутск"),
        new("Asia/Yakutsk", "Якутск"),
        new("Asia/Vladivostok", "Владивосток"),
        new("Asia/Magadan", "Магадан"),
        new("Asia/Kamchatka", "Петропавловск-Камчатский"),
    ];

    /// <summary>Пояса списка, известные этой машине, — в порядке с запада на восток.</summary>
    public static IReadOnlyList<OfferedTimeZone> Offered() =>
        [.. All.Where(z => AppSettingKeys.IsKnownTimeZone(z.Id))];

    /// <summary>Город пояса из списка; <c>null</c> — пояс вне списка, названия у нас для него нет.</summary>
    public static string? City(string? id) =>
        All.FirstOrDefault(z => string.Equals(z.Id, id, StringComparison.Ordinal))?.City;

    /// <summary>
    /// Смещение пояса в заданный момент: «UTC+03:00». Момент нужен, потому что смещение — свойство
    /// не пояса, а пояса в дату: летнее время и переводы стрелок его меняют.
    /// </summary>
    public static string Offset(TimeZoneInfo zone, DateTimeOffset at)
    {
        var offset = zone.GetUtcOffset(at);
        var sign = offset < TimeSpan.Zero ? "−" : "+";
        return $"UTC{sign}{offset.Duration():hh\\:mm}";
    }
}
