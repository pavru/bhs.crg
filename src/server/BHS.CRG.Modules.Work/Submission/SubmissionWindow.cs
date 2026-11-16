using System.Globalization;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Submission;

/// <summary>Предлагаемая пара «дата · смена» нового отчёта (ТЗ MOB-35) и подпись кнопки с сервера.</summary>
public sealed record SuggestedPair(DateOnly Date, ReportShift Shift, string Label);

/// <summary>
/// Правило окна подачи (ТЗ WORK-8, WORK-10, WORK-52, MOB-35; change <c>work-15-submission</c>) — одно место, чистые
/// функции без базы и часов. Закреплено общим файлом примеров <c>Submission/SubmissionCases.json</c> (ТЗ MOB-18): его
/// прогоняют тесты сервера, а в этапе 5 — тесты приложения. Правило, изменённое без файла, роняет тест примеров.
///
/// <para><b>Окно дневной смены</b> кончается в конце даты отчёта, <b>ночной</b> — в конце следующих суток стройки:
/// иначе ночной отчёт подавался бы в полночь посреди смены. Сутки — по поясу стройки, а не компании и не сервера
/// (ТЗ WORK-8).</para>
///
/// <para><b>Конец окна — мгновение</b> (ТЗ WORK-52): его считают при заведении отчёта и при смене даты, смены или
/// стройки и записывают. Пересчёт на чтении по нынешнему поясу сдвинул бы окна от правки пояса задним числом.</para>
/// </summary>
public static class SubmissionWindow
{
    /// <summary>
    /// Конец окна подачи: полночь после даты отчёта (дневная смена) или после следующих суток (ночная) по поясу
    /// стройки. Сутки бывают в 23 и 25 часов — переход на летнее время считает сам пояс.
    /// </summary>
    /// <param name="siteZone">Действующий пояс стройки — имя IANA (<c>ModuleConstruction.TimeZoneId</c>).</param>
    public static DateTimeOffset EndsAt(string siteZone, DateOnly date, ReportShift shift) =>
        StartOfDay(Zone(siteZone), date.AddDays(shift == ReportShift.Night ? 2 : 1));

    /// <summary>
    /// Момент ручной подачи (ТЗ WORK-10): время действия на устройстве, но не позже конца окна, — «ручная подача
    /// или конец окна, что раньше». Раньше заведения отчёта время устройства не проверяется: расхождение часов
    /// показывает очередь приёмки (<c>work-16</c>, ТЗ MOB-17).
    /// </summary>
    public static DateTimeOffset MomentOfManual(DateTimeOffset deviceTime, DateTimeOffset endsAt) =>
        (deviceTime <= endsAt ? deviceTime : endsAt).ToUniversalTime();

    /// <summary>
    /// Предлагаемая пара нового отчёта (ТЗ MOB-35): пока открыто окно вчерашней ночи и у сотрудника есть ночной отчёт
    /// за вчера на этой стройке — «вчера · ночь» («Ночная смена 16.11», смена относится к дате начала, ТЗ WORK-8);
    /// иначе — «сегодня на стройке · день» («Отчёт 17.11»).
    /// </summary>
    /// <param name="hasNightYesterday">У сотрудника есть ночной отчёт за вчерашнюю дату стройки.</param>
    public static SuggestedPair Suggested(string siteZone, DateTimeOffset now, bool hasNightYesterday)
    {
        var zone = Zone(siteZone);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var yesterday = today.AddDays(-1);
        if (hasNightYesterday && now < EndsAt(siteZone, yesterday, ReportShift.Night))
            return new SuggestedPair(yesterday, ReportShift.Night, "Ночная смена " + Short(yesterday));
        return new SuggestedPair(today, ReportShift.Day, "Отчёт " + Short(today));
    }

    private static string Short(DateOnly date) => date.ToString("dd.MM", CultureInfo.InvariantCulture);

    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    /// <summary>
    /// Начало суток в поясе — первое мгновение даты. Полночь, попавшая в разрыв перехода на летнее время, — первое
    /// мгновение после разрыва; полночь, бывшая дважды, — первая из двух.
    /// </summary>
    private static DateTimeOffset StartOfDay(TimeZoneInfo zone, DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
