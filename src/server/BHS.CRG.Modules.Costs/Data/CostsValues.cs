using System.Globalization;
using System.Text.Json;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Разбор присланных значений: текст, дата, деньги, ссылка на запись справочника.
///
/// <para>Извлечено из <see cref="InvoiceRequisites" /> задачей C2 (issue #1078): строки счёта
/// присылают те же деньги и те же ссылки, что шапка. Скопируй их второй раз — и первым разошлось бы
/// именно то, что здесь важнее всего: «1 234,56» из вставки из буфера разбирался бы в шапке и
/// отказывал бы в строке.</para>
///
/// <para><b>Отказы называют ПОЛЕ и ожидаемое</b>, а не «неверный запрос». Читает их человек за формой,
/// и «поле такое-то: ожидается число» отличается от «400» тем, что после него понятно, что делать.</para>
///
/// <para>⚠️ Поэтому у каждого помощника есть <c>label</c> — подпись поля для человека. У реквизитов
/// счёта ключ и подпись совпадают (схема названа по-русски), а у строки счёта ключ приходит из JSON
/// (<c>quantity</c>), и отказ «поле «quantity» — не число» отсылал бы человека за формой к тому, чего
/// он на экране не видит.</para>
/// </summary>
internal static class CostsValues
{
    /// <summary>Формат даты — тот, которым даты хранит и присылает вся система.</summary>
    internal const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// Строка поля — или <c>null</c>, если поля нет либо оно пустое.
    /// </summary>
    /// <param name="limit">Предел длины, если у колонки он есть. Проверяется ЗДЕСЬ, а не в базе:
    /// перебор ловит PostgreSQL внутри <c>SaveChangesAsync</c>, где доменных отказов не бывает, и
    /// наружу уходит 500 без имени поля. Отказ называет поле, предел и присланную длину — по 500
    /// человек не догадается ни о том, ни о другом.</param>
    internal static string? Text(JsonElement source, string key, string? label = null, int? limit = null)
    {
        var text = Value(source, key) switch
        {
            null => null,
            { ValueKind: JsonValueKind.String } value => value.GetString() is { Length: > 0 } found
                ? found
                : null,
            var other => throw Wrong(label ?? key, other, "строку"),
        };

        if (limit is { } max && text is { } sent && sent.Length > max)
            throw new InvalidRequestException(
                $"Поле «{label ?? key}»: {sent.Length} знаков, а вмещается {max}. Сократите значение — " +
                "длиннее хранить негде, и молча обрезать его нельзя: обрезанное разошлось бы с бумагой.");

        return text;
    }

    internal static DateOnly? Date(JsonElement source, string key) => Value(source, key) switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } value =>
            value.GetString() is { Length: > 0 } text
                ? DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out var date)
                    ? date
                    : throw new InvalidRequestException(
                        $"Поле «{key}»: дата «{text}» не разобрана. Ожидается «{DateFormat}» — так её " +
                        "хранят все даты системы, и так её присылает форма.")
                : null,
        var other => throw Wrong(key, other, "дату строкой «" + DateFormat + "»"),
    };

    /// <summary>
    /// Граница величины любого числа модуля — триллион, не включая его.
    ///
    /// <para>Причин две, и обе про отказ с именем поля вместо 500 (находка ревью, issue #1163). Первая —
    /// колонки: <c>numeric(18,2)</c> и <c>numeric(18,3)</c> переполняются внутри <c>SaveChangesAsync</c>,
    /// где доменных отказов не бывает. Вторая — арифметика: количество × цена и сумма × доля считаются в
    /// <c>decimal</c>, и два числа «по колонке» дают произведение, которого в нём нет
    /// (<c>OverflowException</c>). При границе в триллион произведение двух чисел — 10²⁴, с запасом.</para>
    ///
    /// <para>⚠️ Граница НИЖЕ того, что вмещает колонка, и это нарочно: предел «по колонке» спасал бы
    /// только от первой причины. А число в триллион рублей или штук — это склейка при вставке из буфера,
    /// а не счёт: так же устроен предел отсрочки в <see cref="Days" />.</para>
    /// </summary>
    internal const decimal Limit = 1_000_000_000_000m;

    /// <summary>
    /// Число как прислано — разобранное и проверенное на величину, без суждения о знаках после запятой.
    ///
    /// <para>Для тех, у кого о точности свой разговор со своими словами: часть разноски («точнее
    /// тысячной — разошлась бы со строкой»), процент, срок в днях. Остальным — <see cref="Money" /> и
    /// <see cref="Quantity" />.</para>
    /// </summary>
    internal static decimal? Number(JsonElement source, string key, string? label = null)
    {
        var name = label ?? key;
        var number = Value(source, key) switch
        {
            null => (decimal?)null,
            // TryGetDecimal, а не GetDecimal: тот на «1e400» бросает FormatException, а в отказ её
            // никто не отображает — ответом был бы 500.
            { ValueKind: JsonValueKind.Number } value => value.TryGetDecimal(out var parsed)
                ? parsed
                : throw TooLarge(name, value.GetRawText()),
            // Строку принимаем: числа приходят строками и из распознавания, и из вставки из буфера, а
            // отказ на «1 234,56» человек прочтёт как «система не понимает сумм».
            { ValueKind: JsonValueKind.String } value => Parse(name, value.GetString()),
            var other => throw Wrong(name, other, "число"),
        };

        return number is { } sent && Math.Abs(sent) >= Limit
            ? throw TooLarge(name, Shown(sent))
            : number;
    }

    /// <summary>
    /// Деньги — до копеек: так их хранят колонки (<c>numeric(18,2)</c>).
    /// </summary>
    /// <param name="hint">Что делать человеку, если в бумаге число именно такое. Приходит от звавшего:
    /// у цены выход есть (сумму строки можно вписать из бумаги), у суммы к оплате — нет, и общая фраза
    /// была бы советом, по которому некуда идти.</param>
    internal static decimal? Money(JsonElement source, string key, string? label = null, string? hint = null) =>
        Exact(Number(source, key, label), 2, label ?? key, "копейки", hint);

    /// <summary>Количество — до тысячных: так его хранит колонка (<c>numeric(18,3)</c>).</summary>
    internal static decimal? Quantity(JsonElement source, string key, string? label = null) =>
        Exact(Number(source, key, label), 3, label ?? key, "тысячной", null);

    /// <summary>
    /// Лишние знаки после запятой — отказ, а не округление.
    ///
    /// <para>⚠️ Округляет иначе БАЗА, и молча: присланная цена 45,678 ложилась как 45,68, а сумма строки
    /// считалась по неокруглённой. Строка в ответе сама с собой не сходилась (45,68 × 100 ≠ 4567,80),
    /// а повторная отправка той же формы каждый раз писала в журнал «строки изменены» — присланное не
    /// равнялось лежащему. Округлить здесь значило бы то же самое, только на шаг раньше: бумага
    /// поставщика округляет по-своему, и наше число разошлось бы с тем, что человек видит на скане.</para>
    ///
    /// <para>Нули в хвосте лишними знаками не считаются: «45,670» — это 45,67.</para>
    /// </summary>
    private static decimal? Exact(decimal? value, int digits, string label, string finest, string? hint) =>
        value is { } sent && decimal.Round(sent, digits) != sent
            ? throw new InvalidRequestException(
                $"Поле «{label}»: «{Shown(sent)}» точнее {finest}. Точнее " +
                "хранить негде, и молча округлить нельзя: округлённое разошлось бы с бумагой." +
                (hint is null ? string.Empty : " " + hint))
            : value;

    private static InvalidRequestException TooLarge(string label, string sent) =>
        new($"Поле «{label}»: «{(sent.Length > 40 ? sent[..40] + "…" : sent)}» — больше, чем здесь бывает. " +
            "Число ограничено триллионом: такое приносит склейка при вставке из буфера или лишние нули, " +
            "а не бумага поставщика.");

    /// <summary>
    /// Число в отказе — с ЗАПЯТОЙ, как его набирают и как оно стоит в бумаге. «45.678» в ответ на
    /// набранное «45,678» читалось бы как другое число.
    /// </summary>
    internal static string Shown(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture).Replace('.', ',');

    internal static int? Days(JsonElement source, string key) => Number(source, key) switch
    {
        null => null,
        { } value when value == decimal.Truncate(value) && value is >= 0 and < 3651 => (int)value,
        { } value => throw new InvalidRequestException(
            $"Поле «{key}»: «{value}» — не срок в днях. Ожидается целое число от 0 до 3650 " +
            "(десять лет): отсрочка в полдня и отсрочка в век — это опечатка, а не условие поставщика."),
    };

    /// <summary>
    /// Ссылка на запись справочника ядра — <c>{"$ref":"catalog","entryId":"…"}</c>.
    /// </summary>
    /// <param name="why">Почему ссылка, а не текст. Приходит от звавшего, потому что причина у каждого
    /// поля своя: по организации счёт находит ИНН и сопоставление поставщика, по позиции номенклатуры —
    /// затраты, документ качества и то, что отпущено монтажнику. Общая формулировка «так надо» не
    /// сказала бы человеку ничего, а именно её читают в форме.</param>
    internal static Guid? Reference(JsonElement source, string key, string why, string? label = null)
    {
        if (Value(source, key) is not { } value) return null;

        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException(
                $"Поле «{label ?? key}»: ожидается ссылка на запись справочника " +
                "{\"$ref\":\"catalog\",\"entryId\":\"…\"}, пришло " +
                $"{Describe(value)}. {why}");

        // Вид значения проверяется ДО GetString(): у числа он бросает InvalidOperationException, а в
        // отказ его никто не отображает — ответом был бы 500 вместо «поле такое-то не разобрано».
        if (!value.TryGetProperty("entryId", out var entry) || entry.ValueKind != JsonValueKind.String
            || !Guid.TryParse(entry.GetString(), out var id))
            throw new InvalidRequestException(
                $"Поле «{label ?? key}»: в ссылке нет «entryId» со строкой-идентификатором записи справочника. " +
                why);

        return id;
    }

    /// <summary>
    /// Значение поля — или <c>null</c>, если поля нет либо в нём <c>null</c>.
    ///
    /// <para>⚠️ Вид самого <paramref name="source" /> проверяется ЗДЕСЬ, а не только у звавших:
    /// <c>TryGetProperty</c> у не-объекта бросает <c>InvalidOperationException</c>, и одного звавшего,
    /// спросившего поле раньше своей проверки, хватает на 500. Так и было: идентификатор строки читался
    /// до проверки «строка — объект», и <c>{"lines":[null]}</c> отвечал «внутренняя ошибка сервера»
    /// (issue #1163). Звавшие по-прежнему проверяют вид сами — у них есть номер строки, которого здесь
    /// нет; эта проверка — на тот случай, когда порядок снова перепутают.</para>
    /// </summary>
    internal static JsonElement? Value(JsonElement source, string key)
    {
        if (source.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException(
                $"Поле «{key}» читать не из чего: ожидается объект с полями, пришло {Describe(source)}.");

        return source.TryGetProperty(key, out var value) && value.ValueKind is not JsonValueKind.Null
            ? value
            : null;
    }

    /// <summary>
    /// Присланный элемент набора обязан быть объектом — отказ называет его место в наборе.
    /// </summary>
    /// <param name="what">«Строка 3», «Часть 2» — как элемент зовут на экране.</param>
    /// <param name="fields">Чьи поля ожидаются: «строки», «части».</param>
    internal static void EnsureObject(JsonElement source, string what, string fields)
    {
        if (source.ValueKind != JsonValueKind.Object)
            throw new InvalidRequestException(
                $"{what} прислана как {source.ValueKind}, а ожидается объект с полями {fields}.");
    }

    internal static InvalidRequestException Wrong(string key, JsonElement? value, string expected) =>
        new($"Поле «{key}»: ожидается {expected}, пришло {Describe(value)}.");

    /// <summary>
    /// Чем описать пришедшее. Текст называем текстом И ПОКАЗЫВАЕМ: «пришло String» о наименовании
    /// позиции не говорит человеку ничего, а «пришло «кабель ВВГ 3х2,5»» объясняет отказ целиком.
    /// </summary>
    private static string Describe(JsonElement? value) => value switch
    {
        null => "ничего",
        { ValueKind: JsonValueKind.String } text => $"«{text.GetString()}»",
        { } other => other.ValueKind.ToString(),
    };

    private static decimal? Parse(string key, string? text)
    {
        if (text is not { Length: > 0 }) return null;

        var normalized = text.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(',', '.');

        if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return value;

        // Число, которого нет в decimal, — всё равно ЧИСЛО: «не число» о тридцати цифрах подряд отослало
        // бы человека искать лишнюю букву. Тем же стилем, без экспоненты: «1e5» строкой числом не было
        // и не стало.
        if (double.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            throw TooLarge(key, text);

        throw new InvalidRequestException(
                $"Поле «{key}»: «{text}» — не число. Разделителем дробной части понимается и точка, и " +
                "запятая, пробелы внутри числа не мешают; всё остальное разобрать нечем.");
    }
}
