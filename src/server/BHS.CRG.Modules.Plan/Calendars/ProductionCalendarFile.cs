using System.Globalization;
using System.Text;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Modules.Plan.Calendars;

/// <summary>Годовой экспорт PPT.ru. Поддерживаются односуточные события, даты без времени.</summary>
internal static class ProductionCalendarFile
{
    public const int MaxBytes = 128 * 1024;
    private const string Product = "-//PPT.ru//Production calendar//RU";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly HashSet<string> CalendarProperties =
        ["VERSION", "PRODID", "CALSCALE", "METHOD", "X-WR-CALNAME", "X-WR-CALDESC"];
    private static readonly HashSet<string> EventProperties =
        ["UID", "DTSTAMP", "DTSTART;VALUE=DATE", "DTEND;VALUE=DATE", "SUMMARY", "DESCRIPTION", "TRANSP"];

    public static async Task<Dictionary<DateOnly, CalendarDayKind>> ReadAsync(Stream stream, int year, CancellationToken ct)
    {
        string content;
        try
        {
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            content = await reader.ReadToEndAsync(ct);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidRequestException("Файл календаря должен быть сохранён в UTF-8.");
        }

        using var lines = Unfold(content.TrimStart('\uFEFF')).GetEnumerator();
        if (!lines.MoveNext() || lines.Current.Value != "BEGIN:VCALENDAR")
            throw Bad(1, "ожидалось начало календаря iCalendar.");

        var header = new Dictionary<string, Property>();
        var days = new Dictionary<DateOnly, CalendarDayKind>();
        Dictionary<string, Property>? entry = null;
        var lastLine = 1;
        while (lines.MoveNext())
        {
            var (line, value) = lines.Current;
            lastLine = line;
            switch (value)
            {
                case "BEGIN:VEVENT" when entry is null:
                    CheckHeader(header, line);
                    entry = [];
                    break;
                case "END:VEVENT" when entry is not null:
                    ReadEvent(entry, days, year, line);
                    entry = null;
                    break;
                case "END:VCALENDAR" when entry is null:
                    CheckHeader(header, line);
                    if (days.Count == 0) throw Bad(line, "нет исключений на выбранный год.");
                    if (lines.MoveNext()) throw Bad(lines.Current.Number, "лишние данные после календаря.");
                    return days;
                default:
                    var field = value.IndexOf(':', StringComparison.Ordinal);
                    var allowed = entry is not null ? EventProperties : CalendarProperties;
                    if (field < 1 || !allowed.Contains(value[..field]))
                        throw Bad(line, "нераспознанная строка файла PPT.ru.");
                    if (!(entry ?? header).TryAdd(value[..field], new(line, value[(field + 1)..])))
                        throw Bad(line, "свойство уже задано.");
                    break;
            }
        }
        throw Bad(lastLine, "календарь оборван, ожидалось END:VCALENDAR.");
    }

    private static void CheckHeader(Dictionary<string, Property> header, int line)
    {
        var version = Read(header, "VERSION", line);
        if (version.Value != "2.0") throw Bad(version.Number, "ожидалась версия iCalendar 2.0.");
        var product = Read(header, "PRODID", line);
        if (product.Value != Product) throw Bad(product.Number, "ожидалась выгрузка производственного календаря PPT.ru.");
    }

    private static void ReadEvent(Dictionary<string, Property> entry, Dictionary<DateOnly, CalendarDayKind> days,
        int year, int line)
    {
        const string dateFormat = "yyyyMMdd";
        var start = Read(entry, "DTSTART;VALUE=DATE", line);
        if (!DateOnly.TryParseExact(start.Value, dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw Bad(start.Number, "ожидалась дата в формате ГГГГММДД.");
        if (date.Year != year) throw Bad(start.Number, $"дата не относится к выбранному {year} году.");
        var end = Read(entry, "DTEND;VALUE=DATE", line);
        if (!DateOnly.TryParseExact(end.Value, dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var next)
            || next.DayNumber != date.DayNumber + 1)
            throw Bad(end.Number, "событие должно занимать один календарный день.");

        var uid = Read(entry, "UID", line);
        var kind = uid.Value.ReplacePrefix(start.Value + "-") switch
        {
            "holiday@ppt.ru" or "dayoff@ppt.ru" => CalendarDayKind.Holiday,
            "short@ppt.ru" => CalendarDayKind.Short,
            _ => throw Bad(uid.Number, "нераспознанный вид дня или дата в UID не совпадает с датой события."),
        };
        if (!days.TryAdd(date, kind)) throw Bad(start.Number, "исключение на эту дату уже есть в файле.");
    }

    private static Property Read(Dictionary<string, Property> values, string field, int line) =>
        values.GetValueOrDefault(field) ?? throw Bad(line, $"отсутствует свойство {field}.");

    private static IEnumerable<Property> Unfold(string text)
    {
        using var reader = new StringReader(text);
        Property? current = null;
        for (var number = 1; reader.ReadLine() is { } line; number++)
        {
            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                if (current is null) throw Bad(number, "продолжение строки без начала.");
                current = current with { Value = current.Value + line[1..] };
            }
            else
            {
                if (current is not null) yield return current;
                current = new(number, line);
            }
        }
        if (current is not null) yield return current;
    }

    private static string ReplacePrefix(this string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : string.Empty;

    private static InvalidRequestException Bad(int line, string reason) =>
        new($"Файл календаря, строка {line}: {reason}");

    private sealed record Property(int Number, string Value);
}
