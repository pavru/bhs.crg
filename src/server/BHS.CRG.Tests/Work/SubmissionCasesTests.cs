using System.Globalization;
using System.Text.Json;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Submission;

namespace BHS.CRG.Tests.Work;

/// <summary>
/// Правило окна подачи по общему файлу примеров (ТЗ MOB-18, WORK-8, WORK-10, WORK-52, MOB-35; change
/// <c>work-15-submission</c>). Файл лежит рядом с правилом (<c>Submission/SubmissionCases.json</c>), его же
/// читают тесты приложения: правило, изменённое без файла, краснеет здесь с названием примера.
///
/// <para><b>Состав обязателен.</b> Без ночной смены и без пояса стройки, отличного от пояса компании, примеры прошли бы
/// и у правила, которое считает сутки по компании и закрывает ночь в полночь, — то есть ровно у тех ошибок, ради
/// которых файл заведён.</para>
/// </summary>
public class SubmissionCasesTests
{
    private static readonly string CasesFile =
        RepoPaths.File("src", "server", "BHS.CRG.Modules.Work", "Submission", "SubmissionCases.json");

    public static TheoryData<string> Names() => [.. Cases().Select(c => Text(c, "name"))];

    [Theory]
    [MemberData(nameof(Names))]
    public void Пример_окна_совпадает_с_правилом(string name)
    {
        var c = Cases().Single(x => Text(x, "name") == name);
        var expect = c.GetProperty("expect");
        var zone = EffectiveZone(c);
        var shift = Report.ParseShift(c.GetProperty("shift").GetString());

        var ends = SubmissionWindow.EndsAt(zone, Day(Text(c, "date")), shift);
        Assert.Equal(Instant(expect, "windowEndsAt"), ends);
        Assert.Equal(Instant(expect, "momentOfManual"), SubmissionWindow.MomentOfManual(Instant(c, "deviceTime"), ends));

        var suggested = expect.GetProperty("suggested");
        var pair = SubmissionWindow.Suggested(zone, Instant(c, "now"), c.GetProperty("hasNightYesterday").GetBoolean());
        Assert.Equal(Day(Text(suggested, "date")), pair.Date);
        Assert.Equal(Text(suggested, "shift"), Report.ShiftCode(pair.Shift));
        Assert.Equal(Text(suggested, "label"), pair.Label);

        if (c.TryGetProperty("attemptedShiftAfterSubmission", out var attempted))
            AssertShiftLocked(c, shift, Report.ParseShift(attempted.GetString()), ends);
    }

    private static void AssertShiftLocked(JsonElement c, ReportShift shift, ReportShift attempted, DateTimeOffset ends)
    {
        var now = Instant(c, "deviceTime");
        var site = Guid.NewGuid();
        var date = Day(Text(c, "date"));
        var report = Report.Create(Guid.NewGuid(), Guid.NewGuid(), site, date, shift, null, null, 1, now, ends);
        report.Submit(SubmissionWindow.MomentOfManual(now, ends), ReportSubmitter.Manual, null, 2, now);

        Assert.NotEqual(shift, attempted);
        Assert.Equal(Report.KeyLocked, Assert.Throws<ConflictException>(() =>
            report.ChangeHeader(site, date, attempted, null, null, 3, now,
                SubmissionWindow.EndsAt(EffectiveZone(c), date, attempted))).Message);
        Assert.Equal(shift, report.Shift);
        Assert.Equal(ends, report.WindowEndsAt);
    }

    /// <summary>Ночная смена, чужой пояс, «после полуночи в ночную смену» и переход часов — в файле есть все.</summary>
    [Fact]
    public void Состав_файла_примеров()
    {
        var cases = Cases();
        Assert.Contains(cases, c => Text(c, "shift") == "night");
        Assert.Contains(cases, c => c.GetProperty("shift").ValueKind == JsonValueKind.Null);
        Assert.Contains(cases, c => c.TryGetProperty("attemptedShiftAfterSubmission", out _));
        Assert.Contains(cases, c => EffectiveZone(c) != Text(c, "companyZone"));
        Assert.Contains(cases, c => c.GetProperty("siteZone").GetString() == "Europe/Berlin");
        Assert.Contains(cases, AfterMidnightInNight);
        Assert.Contains(cases, c => c.GetProperty("siteZone").ValueKind == JsonValueKind.Null && AfterMidnightInNight(c));
        Assert.Equal(cases.Count, cases.Select(c => Text(c, "name")).Distinct().Count());
    }

    /// <summary>Дневная смена кончается в конце даты отчёта по поясу стройки.</summary>
    [Fact]
    public void Дневная_смена_кончается_в_конце_даты()
    {
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 21, 0, 0, TimeSpan.Zero),
            SubmissionWindow.EndsAt("Europe/Moscow", new DateOnly(2026, 11, 16), ReportShift.Day));
    }

    /// <summary>Ночная — в конце следующих суток стройки: во Владивостоке 00:00 18.11 — это 17:00 17.11 по Москве.</summary>
    [Fact]
    public void Ночная_смена_кончается_в_конце_следующих_суток_стройки()
    {
        var ends = SubmissionWindow.EndsAt("Asia/Vladivostok", new DateOnly(2026, 11, 16), ReportShift.Night);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 17, 0, 0, TimeSpan.FromHours(3)), ends);
    }

    /// <summary>Ручная подача после конца окна даёт конец окна, а не время устройства.</summary>
    [Fact]
    public void Ручная_подача_после_конца_окна_даёт_конец_окна()
    {
        var ends = SubmissionWindow.EndsAt("Europe/Moscow", new DateOnly(2026, 11, 16), ReportShift.Day);
        var late = new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.FromHours(3));
        Assert.Equal(ends, SubmissionWindow.MomentOfManual(late, ends));
        Assert.Equal(TimeSpan.Zero, SubmissionWindow.MomentOfManual(late, ends).Offset);
    }

    /// <summary>«Сейчас» — на следующий день стройки после даты отчёта, а предложено продолжить ночь.</summary>
    private static bool AfterMidnightInNight(JsonElement c)
    {
        var suggested = c.GetProperty("expect").GetProperty("suggested");
        if (Text(suggested, "shift") != "night") return false;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(EffectiveZone(c));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Instant(c, "now"), zone).DateTime);
        return today == Day(Text(suggested, "date")).AddDays(1);
    }

    private static List<JsonElement> Cases()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(CasesFile));
        return [.. file.RootElement.GetProperty("cases").EnumerateArray().Select(c => c.Clone())];
    }

    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static string EffectiveZone(JsonElement c) => c.GetProperty("siteZone").GetString() ?? Text(c, "companyZone");

    private static DateOnly Day(string iso) => DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset Instant(JsonElement e, string name) =>
        DateTimeOffset.Parse(Text(e, name), CultureInfo.InvariantCulture);
}
