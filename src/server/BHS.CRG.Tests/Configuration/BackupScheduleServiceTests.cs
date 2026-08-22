using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Backup;

namespace BHS.CRG.Tests.Configuration;

/// <summary>
/// Правило планового копирования (issue #832): когда служба считает, что настал срок.
///
/// Проверяется тестами, а не наблюдением за ночным сервером: единственный способ увидеть
/// «пропущенный запуск» вживую — выключить машину на ночь, а ошибиться здесь легко и незаметно.
/// Ошибка в любую сторону дорога: не сработает — копий не будет вовсе; сработает лишний раз — база
/// читается целиком посреди рабочего дня.
/// </summary>
public class BackupScheduleServiceTests
{
    private static readonly TimeSpan Msk = TimeSpan.FromHours(3);

    private static DateTimeOffset At(int day, int hour, int minute = 0)
        => new(2026, 8, day, hour, minute, 0, Msk);

    private static BackupScheduleSettings Schedule(
        bool enabled = true, string time = "03:00", int keep = 7)
        => new() { Enabled = enabled, TimeOfDay = time, KeepCount = keep };

    [Fact]
    public void BeforeTheHour_NotDue()
        => Assert.False(BackupScheduleService.IsDue(Schedule(), lastRunAt: null, At(20, 2, 59)));

    /// <summary>
    /// Свежая установка: копий не было никогда. Ждать до завтрашней ночи нельзя — система,
    /// проработавшая день без единой копии, ровно то состояние, ради которого расписание и заведено.
    /// </summary>
    [Fact]
    public void NeverRun_DueOnceTheHourPassed()
        => Assert.True(BackupScheduleService.IsDue(Schedule(), lastRunAt: null, At(20, 3, 1)));

    [Fact]
    public void AlreadyRunToday_NotDueAgain()
        => Assert.False(BackupScheduleService.IsDue(Schedule(), At(20, 3, 0), At(20, 23, 59)));

    [Fact]
    public void RunYesterday_DueToday()
        => Assert.True(BackupScheduleService.IsDue(Schedule(), At(19, 3, 0), At(20, 3, 0)));

    /// <summary>
    /// Сервер был выключен ночью и включён утром: срок сегодняшних суток прошёл, копию не ставили —
    /// снимаем утром. Ждать следующей ночи значило бы оставить сутки без копии из-за перезагрузки.
    /// </summary>
    [Fact]
    public void ServerWasOffAtNight_DueAtMorningStart()
        => Assert.True(BackupScheduleService.IsDue(Schedule(), At(19, 3, 0), At(20, 9, 30)));

    /// <summary>
    /// Неделя простоя — ОДНА копия, а не семь. Правило «сегодняшний срок прошёл, последняя
    /// постановка была раньше него» само это обеспечивает: сразу после постановки условие ложно до
    /// завтрашнего срока.
    /// </summary>
    [Fact]
    public void WeekOfDowntime_CatchesUpOnlyOnce()
    {
        var now = At(20, 9, 30);
        Assert.True(BackupScheduleService.IsDue(Schedule(), At(13, 3, 0), now));
        // Служба отметила срок — второй раз в те же сутки не сработает.
        Assert.False(BackupScheduleService.IsDue(Schedule(), now, now.AddMinutes(1)));
        Assert.False(BackupScheduleService.IsDue(Schedule(), now, At(20, 23, 59)));
        // ...а завтра — снова да.
        Assert.True(BackupScheduleService.IsDue(Schedule(), now, At(21, 3, 0)));
    }

    [Fact]
    public void Disabled_NeverDue()
        => Assert.False(BackupScheduleService.IsDue(Schedule(enabled: false), lastRunAt: null, At(20, 23, 0)));

    /// <summary>
    /// Негодное время — НЕ срабатывание «когда-нибудь», а молчание: подставлять умолчание вместо
    /// того, что задал человек, значит снимать копию не тогда, когда он думает.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("ночью")]
    [InlineData("25:00")]
    [InlineData("03:60")]
    public void InvalidTime_NeverDue(string time)
        => Assert.False(BackupScheduleService.IsDue(Schedule(time: time), lastRunAt: null, At(20, 23, 0)));

    [Theory]
    [InlineData("03:00", 3, 0)]
    [InlineData("00:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    [InlineData(" 04:30 ", 4, 30)]
    public void ParseTimeOfDay_Accepts(string value, int hours, int minutes)
        => Assert.Equal(new TimeSpan(hours, minutes, 0), BackupScheduleService.ParseTimeOfDay(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3:00")]
    [InlineData("24:00")]
    [InlineData("03:00:00")]
    public void ParseTimeOfDay_Rejects(string? value)
        => Assert.Null(BackupScheduleService.ParseTimeOfDay(value));
}
