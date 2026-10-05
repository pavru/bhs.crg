using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace BHS.CRG.Domain.Periods;

/// <summary>Чем считают документы строки перечня: «счёт», «счёта», «счетов».</summary>
public sealed record ClosingUnit(string One, string Few, string Many)
{
    /// <summary>«3 счёта», «1 счёт», «5 счетов».</summary>
    public string Text(int count)
    {
        int tens = count % 100, ones = count % 10;
        var word = tens is >= 11 and <= 14 ? Many : ones == 1 ? One : ones is >= 2 and <= 4 ? Few : Many;
        return $"{count.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))} {word}";
    }
}

/// <summary>Строка перечня: что именно и сколько.</summary>
/// <param name="Key">Ключ строки внутри раздела — по нему экран сопоставляет «было» и «стало».</param>
/// <param name="Text">Что это: «Оплачены в периоде, но не разнесены или не разобраны».</param>
/// <param name="Count">Сколько документов.</param>
/// <param name="Amount">На сколько; null — строка денег не несёт.</param>
/// <param name="AmountPermission">Право, открывающее сумму. Суммы модуля закрыты его правом, а диалог —
/// экран ядра: без этого права человеку показывают только число документов. null — сумма открыта всем,
/// кому открыт диалог.</param>
/// <param name="Note">Пояснение под строкой; null — нет.</param>
/// <param name="Link">Адрес экрана приложения с этими документами, от корня; null — ссылки нет.</param>
public sealed record ClosingLine(
    string Key, string Text, int Count, ClosingUnit Unit, decimal? Amount, string? AmountPermission, string? Note,
    string? Link = null);

/// <summary>Раздел перечня — один модуль.</summary>
/// <param name="Module">Код модуля.</param>
/// <param name="Title">Название модуля для человека — как его называет реестр модулей.</param>
/// <param name="DateRule">По какой дате модуль относит документ к периоду (ТЗ CORE-35).</param>
/// <param name="Unfinished">Не завершено: закрытию не мешает, но после него останется как есть.</param>
/// <param name="Frozen">Что войдёт в закрытый период.</param>
public sealed record ClosingSection(
    string Module, string Title, string DateRule,
    IReadOnlyList<ClosingLine> Unfinished, IReadOnlyList<ClosingLine> Frozen);

/// <summary>
/// Что показал диалог закрытия периода: по каждому включённому модулю — что войдёт в закрытый период и
/// что не завершено (ТЗ CORE-35; задача E1b, issue #1099).
///
/// <para><b>Живёт в самой записи закрытия</b> (<see cref="PeriodClosure.Report" />), а не в журнале
/// (решение владельца 05.10.2026): запись неизменяема, едет в резервную копию, и её читает «История».
/// Журнал ядра читают по одному общему праву, а суммы модуля закрыты его правом, — туда идёт текст без
/// рублей (<see cref="JournalText" />), отрисованный из этого же перечня.</para>
///
/// <para><b>Отпечаток</b> (<see cref="Stamp" />) — то, чем закрывающий говорит «я видел именно это».
/// Закрытие пересчитывает перечень под своим замком и отказывает, если отпечаток другой: иначе в запись
/// легло бы то, чего человек не видел, — счёт, оплаченный секунду назад.</para>
/// </summary>
public sealed record ClosingReport(IReadOnlyList<ClosingSection> Sections)
{
    public static ClosingReport Empty { get; } = new([]);

    // Кириллица — как есть: запись читают и глазами, а «О…» в колонке не читает никто.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Перечень из записи. null — перечня у записи нет: закрытие сделано до E1b либо это отмена.
    /// Неразобранное — тоже null, а не отказ: «История» обязана открываться и с записью, приехавшей
    /// копией от другой версии.
    ///
    /// <para>⚠️ «Разобралось» — не «годно»: <c>{"sections":[{}]}</c> десериализуется без ошибки, а
    /// разделом без строк и строкой без единицы счёта. Такой перечень уронил бы «Историю» целиком, на
    /// первой же записи (ревью PR #1201), — поэтому годность проверяется здесь, до последнего поля.</para>
    /// </summary>
    public static ClosingReport? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<ClosingReport>(json, Json) is { } report && Sound(report) ? report : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Типы объявлены ненулевыми, но десериализатор этого не знает: пропущенное поле — null.
    private static bool Sound(ClosingReport? report) =>
        report?.Sections is { } sections && sections.All(s =>
            s is { Module: not null, Title: not null, DateRule: not null, Unfinished: not null, Frozen: not null }
            && s.Unfinished.Concat(s.Frozen).All(l =>
                l is { Key: not null, Text: not null, Unit: { One: not null, Few: not null, Many: not null } }));

    /// <summary>
    /// Отпечаток увиденного: контур, даты и весь перечень С СУММАМИ — и у того, кому суммы не
    /// показаны. Отпечаток не должен зависеть от того, кто смотрит: иначе двое, глядя на одно и то же,
    /// подтверждали бы разное.
    /// </summary>
    public string Stamp(PeriodContour contour, DateOnly from, DateOnly through)
    {
        var seen = $"{contour.Kind}|{contour.ConstructionId}|{from:yyyy-MM-dd}|{through:yyyy-MM-dd}|{ToJson()}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seen)))[..32];
    }

    /// <summary>
    /// Незавершённое — словами для журнала ядра: числа документов, без рублей. null — незавершённого
    /// нет, и событию нечего добавить к контуру и датам.
    /// </summary>
    public string? JournalText()
    {
        var lines = Sections
            .SelectMany(s => s.Unfinished.Where(l => l.Count > 0).Select(l => $"{s.Title} — {Lower(l.Text)}: {l.Unit.Text(l.Count)}"))
            .ToList();
        return lines.Count == 0 ? null : "Не завершено: " + string.Join("; ", lines);
    }

    /// <summary>
    /// Перечень так, как его можно показать человеку с такими правами: сумма строки без её права
    /// убрана. Режет ядро, а не модуль: модуль всегда отдаёт перечень целиком — иначе запись и
    /// отпечаток зависели бы от того, кто закрывает.
    /// </summary>
    public ClosingReport VisibleTo(IReadOnlyCollection<string> permissions)
    {
        ClosingLine Cut(ClosingLine line) =>
            line.AmountPermission is { } needed && !permissions.Contains(needed) ? line with { Amount = null } : line;

        return new([.. Sections.Select(s => s with { Unfinished = [.. s.Unfinished.Select(Cut)], Frozen = [.. s.Frozen.Select(Cut)] })]);
    }

    /// <summary>Скрыта ли в разделе хоть одна сумма — тогда экран говорит об этом одной строкой.</summary>
    public static bool HidesAmounts(ClosingSection shown, ClosingSection full) =>
        full.Unfinished.Concat(full.Frozen).Count(l => l.Amount is not null)
        != shown.Unfinished.Concat(shown.Frozen).Count(l => l.Amount is not null);

    private static string Lower(string text) =>
        text.Length == 0 ? text : char.ToLower(text[0], CultureInfo.GetCultureInfo("ru-RU")) + text[1..];
}
