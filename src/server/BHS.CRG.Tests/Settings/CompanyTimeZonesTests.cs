using BHS.CRG.Application.Settings;

namespace BHS.CRG.Tests.Settings;

/// <summary>
/// Пояса, которые экран настройки предлагает на выбор (issue #1148).
/// </summary>
public class CompanyTimeZonesTests
{
    private static readonly DateTimeOffset Winter = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Предложить пояс, которого на машине нет, значило бы предложить отказ: сохранение ответило бы
    /// 400. Поэтому список — только известное этой системе, и каждый его пункт обязан разбираться.
    /// </summary>
    [Fact]
    public void Every_offered_zone_is_known_to_this_machine()
    {
        var offered = CompanyTimeZones.Offered();

        Assert.NotEmpty(offered);
        Assert.All(offered, z => Assert.True(AppSettingKeys.IsKnownTimeZone(z.Id), z.Id));
        Assert.Contains(offered, z => z.Id == "Europe/Moscow" && z.City == "Москва");
    }

    /// <summary>
    /// Порядок — с запада на восток: список читают глазами, и «Москва» после «Владивостока»
    /// выглядела бы случайной. Проверяется смещением, а не порядком строк в коде.
    /// </summary>
    [Fact]
    public void Offered_zones_go_from_west_to_east()
    {
        var offsets = CompanyTimeZones.Offered()
            .Select(z => TimeZoneInfo.FindSystemTimeZoneById(z.Id).GetUtcOffset(Winter))
            .ToList();

        Assert.Equal(offsets.OrderBy(o => o), offsets);
    }

    [Fact]
    public void A_zone_outside_the_list_has_no_city()
    {
        Assert.Equal("Владивосток", CompanyTimeZones.City("Asia/Vladivostok"));
        Assert.Null(CompanyTimeZones.City("Asia/Novosibirsk"));
        Assert.Null(CompanyTimeZones.City(null));
    }

    [Theory]
    [InlineData("Europe/Moscow", "UTC+03:00")]
    [InlineData("Asia/Kamchatka", "UTC+12:00")]
    [InlineData("UTC", "UTC+00:00")]
    [InlineData("America/New_York", "UTC−05:00")]
    [InlineData("Asia/Kolkata", "UTC+05:30")]
    public void Offset_is_written_with_sign_and_minutes(string id, string expected)
    {
        Assert.Equal(expected, CompanyTimeZones.Offset(TimeZoneInfo.FindSystemTimeZoneById(id), Winter));
    }

    /// <summary>Смещение — свойство пояса в дату, а не пояса: летом Нью-Йорк на час ближе.</summary>
    [Fact]
    public void Offset_follows_the_date()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var summer = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("UTC−04:00", CompanyTimeZones.Offset(newYork, summer));
    }
}
