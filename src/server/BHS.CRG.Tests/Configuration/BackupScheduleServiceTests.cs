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
    private static readonly TimeZoneInfo Moscow = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private static readonly TimeZoneInfo Vladivostok = TimeZoneInfo.FindSystemTimeZoneById("Asia/Vladivostok");
    private static readonly TimeZoneInfo Kaliningrad = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kaliningrad");

    /// <summary>Правило по поясу компании «Москва» — так считались все проверки до issue #1468.</summary>
    private static bool DueInMoscow(BackupScheduleSettings schedule, DateTimeOffset? lastRunAt, DateTimeOffset now)
        => BackupScheduleService.IsDue(schedule, lastRunAt, now, Moscow);

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0)
        => new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int day, int hour, int minute = 0)
        => new(2026, 8, day, hour, minute, 0, Msk);

    private static BackupScheduleSettings Schedule(
        bool enabled = true, string time = "03:00", int keep = 7)
        => new() { Enabled = enabled, TimeOfDay = time, KeepCount = keep };

    [Fact]
    public void BeforeTheHour_NotDue()
        => Assert.False(DueInMoscow(Schedule(), lastRunAt: null, At(20, 2, 59)));

    /// <summary>
    /// Свежая установка: копий не было никогда. Ждать до завтрашней ночи нельзя — система,
    /// проработавшая день без единой копии, ровно то состояние, ради которого расписание и заведено.
    /// </summary>
    [Fact]
    public void NeverRun_DueOnceTheHourPassed()
        => Assert.True(DueInMoscow(Schedule(), lastRunAt: null, At(20, 3, 1)));

    [Fact]
    public void AlreadyRunToday_NotDueAgain()
        => Assert.False(DueInMoscow(Schedule(), At(20, 3, 0), At(20, 23, 59)));

    [Fact]
    public void RunYesterday_DueToday()
        => Assert.True(DueInMoscow(Schedule(), At(19, 3, 0), At(20, 3, 0)));

    /// <summary>
    /// Сервер был выключен ночью и включён утром: срок сегодняшних суток прошёл, копию не ставили —
    /// снимаем утром. Ждать следующей ночи значило бы оставить сутки без копии из-за перезагрузки.
    /// </summary>
    [Fact]
    public void ServerWasOffAtNight_DueAtMorningStart()
        => Assert.True(DueInMoscow(Schedule(), At(19, 3, 0), At(20, 9, 30)));

    /// <summary>
    /// Неделя простоя — ОДНА копия, а не семь. Правило «сегодняшний срок прошёл, последняя
    /// постановка была раньше него» само это обеспечивает: сразу после постановки условие ложно до
    /// завтрашнего срока.
    /// </summary>
    [Fact]
    public void WeekOfDowntime_CatchesUpOnlyOnce()
    {
        var now = At(20, 9, 30);
        Assert.True(DueInMoscow(Schedule(), At(13, 3, 0), now));
        // Служба отметила срок — второй раз в те же сутки не сработает.
        Assert.False(DueInMoscow(Schedule(), now, now.AddMinutes(1)));
        Assert.False(DueInMoscow(Schedule(), now, At(20, 23, 59)));
        // ...а завтра — снова да.
        Assert.True(DueInMoscow(Schedule(), now, At(21, 3, 0)));
    }

    [Fact]
    public void Disabled_NeverDue()
        => Assert.False(DueInMoscow(Schedule(enabled: false), lastRunAt: null, At(20, 23, 0)));

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
        => Assert.False(DueInMoscow(Schedule(time: time), lastRunAt: null, At(20, 23, 0)));

    /// <summary>
    /// Сторож задачи (issue #1468): время — по поясу КОМПАНИИ. Владивосток (UTC+10), 03:00 — это
    /// 17:00 UTC накануне. Пояс сервера правилу не виден вовсе, поэтому тест от него не зависит.
    /// </summary>
    [Fact]
    public void CompanyZoneDiffersFromServer_DueAtCompanyTime()
    {
        Assert.False(BackupScheduleService.IsDue(Schedule(), null, Utc(8, 19, 16, 59), Vladivostok));
        Assert.True(BackupScheduleService.IsDue(Schedule(), null, Utc(8, 19, 17, 0), Vladivostok));
    }

    /// <summary>
    /// Один момент — один ответ, каким бы смещением его ни записали. Раньше срок строился со
    /// смещением «сейчас»: момент из часов сервера в UTC и тот же момент по Москве давали разное.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-5)]
    public void SameInstant_SameAnswerWhateverOffset(int offsetHours)
    {
        var offset = TimeSpan.FromHours(offsetHours);
        var before = Utc(8, 20, 23, 59).ToOffset(offset);   // 02:59 МСК 21-го
        var after = Utc(8, 21, 0, 0).ToOffset(offset);      // 03:00 МСК 21-го
        Assert.False(DueInMoscow(Schedule(), Utc(8, 20, 0, 0), before));
        Assert.True(DueInMoscow(Schedule(), Utc(8, 20, 0, 0), after));
    }

    /// <summary>
    /// Весна, Берлин, 29.03.2026: в 02:00 часы переводят на 03:00, и 02:30 в эти сутки нет. Копия —
    /// в первую минуту после разрыва (03:00 CEST = 01:00 UTC), одна.
    /// </summary>
    [Fact]
    public void SpringGap_DueRightAfterTheGap_Once()
    {
        var at0230 = Schedule(time: "02:30");
        Assert.False(BackupScheduleService.IsDue(at0230, Utc(3, 28, 1, 30), Utc(3, 29, 0, 59), Berlin));
        Assert.True(BackupScheduleService.IsDue(at0230, Utc(3, 28, 1, 30), Utc(3, 29, 1, 0), Berlin));
        Assert.False(BackupScheduleService.IsDue(at0230, Utc(3, 29, 1, 0), Utc(3, 29, 21, 59), Berlin));
    }

    /// <summary>
    /// Осень, Берлин, 25.10.2026: 02:30 бывает дважды (00:30 и 01:30 UTC). Копия — в первый раз;
    /// во второй — нет: срок у суток один.
    /// </summary>
    [Fact]
    public void AutumnOverlap_DueTheFirstTime_NotAgain()
    {
        var at0230 = Schedule(time: "02:30");
        Assert.True(BackupScheduleService.IsDue(at0230, Utc(10, 24, 0, 30), Utc(10, 25, 0, 30), Berlin));
        Assert.False(BackupScheduleService.IsDue(at0230, Utc(10, 25, 0, 30), Utc(10, 25, 1, 30), Berlin));
    }

    /// <summary>
    /// Регресс прежнего правила: срок строился со смещением «сейчас». В день осеннего перевода копия
    /// в 00:00 (22:00 UTC накануне, ещё летнее время) после перевода «переезжала» на 23:00 UTC — и
    /// снималась второй раз.
    /// </summary>
    [Fact]
    public void AutumnChange_EarlierCopyNotRepeatedAfterTheChange()
        => Assert.False(BackupScheduleService.IsDue(
            Schedule(time: "00:00"), Utc(10, 24, 22, 0), Utc(10, 25, 1, 5), Berlin));

    /// <summary>
    /// Смена пояса после сегодняшней копии (03:00 МСК = 00:00 UTC). На восток (Владивосток) —
    /// сегодняшний срок уже за копией, второй нет. На запад (Калининград) — по новому поясу срок
    /// 01:00 UTC, а копия была раньше: ещё одна. Лишняя, а не потерянная — решение принято сознательно.
    /// </summary>
    [Fact]
    public void ZoneChangedAfterTodaysCopy_EastNoCopy_WestOneMore()
    {
        Assert.False(BackupScheduleService.IsDue(Schedule(), Utc(8, 20, 0, 0), Utc(8, 20, 9, 0), Vladivostok));
        Assert.True(BackupScheduleService.IsDue(Schedule(), Utc(8, 20, 0, 0), Utc(8, 20, 9, 0), Kaliningrad));
    }

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

/// <summary>
/// Сколько плановых копий оставить перед снятием новой (issue #832).
///
/// Правило кажется арифметикой, но ошибка в нём даёт не кривое число, а <b>вечный ночной отказ</b>:
/// уборка освобождает место «по бюджету расписания», каталог занят ещё и ручными копиями, места не
/// хватает — и так каждую ночь, с уведомлением об ошибке каждое утро. Считается от фактической
/// занятости каталога именно поэтому.
/// </summary>
public class BackupPruneTargetTests
{
    /// <summary>Обычная ночь: плановых ровно предел, места в каталоге хватает — уходит одна старая.</summary>
    [Fact]
    public void FullSchedule_FreesExactlyOneSlot()
        => Assert.Equal(6, BackupJobRunner.PruneTargetBeforeExport(
            keep: 7, managedPresent: 7, totalPresent: 7, capacity: 10));

    /// <summary>
    /// Каталог занят ещё и ручными копиями. Считать по одному лишь бюджету расписания значило бы
    /// освобождать место, которого не хватает ровно на эти ручные, — вечный отказ.
    /// </summary>
    [Fact]
    public void ManualCopiesOccupySpace_PruneGoesDeeper()
        => Assert.Equal(8, BackupJobRunner.PruneTargetBeforeExport(
            keep: 10, managedPresent: 9, totalPresent: 10, capacity: 10));

    /// <summary>Место есть — глубже бюджета расписания не убираем.</summary>
    [Fact]
    public void PlentyOfRoom_KeepsBudgetMinusOne()
        => Assert.Equal(2, BackupJobRunner.PruneTargetBeforeExport(
            keep: 3, managedPresent: 3, totalPresent: 3, capacity: 10));

    /// <summary>
    /// Последняя плановая не удаляется НИКОГДА — даже когда места не хватает. Между уборкой и новой
    /// копией система не должна оставаться вовсе без копий; отказ по вместимости придёт громко.
    /// </summary>
    [Theory]
    [InlineData(1, 1, 2, 2)]
    [InlineData(1, 1, 1, 1)]
    [InlineData(2, 1, 5, 3)]
    public void NeverBelowOne(int keep, int managed, int total, int capacity)
        => Assert.Equal(1, BackupJobRunner.PruneTargetBeforeExport(keep, managed, total, capacity));

    /// <summary>Плановых ещё нет — убирать нечего, и правило не должно требовать невозможного.</summary>
    [Fact]
    public void NothingManagedYet_NothingToPrune()
        => Assert.Equal(1, BackupJobRunner.PruneTargetBeforeExport(
            keep: 7, managedPresent: 0, totalPresent: 3, capacity: 10));
}
