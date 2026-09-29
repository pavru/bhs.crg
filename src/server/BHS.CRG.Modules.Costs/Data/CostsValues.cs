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

    internal static string? Text(JsonElement source, string key, string? label = null) => Value(source, key) switch
    {
        null => null,
        { ValueKind: JsonValueKind.String } value => value.GetString() is { Length: > 0 } text ? text : null,
        var other => throw Wrong(label ?? key, other, "строку"),
    };

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

    internal static decimal? Money(JsonElement source, string key, string? label = null) => Value(source, key) switch
    {
        null => null,
        { ValueKind: JsonValueKind.Number } value => value.GetDecimal(),
        // Строку принимаем: числа приходят строками и из распознавания, и из вставки из буфера, а
        // отказ на «1 234,56» человек прочтёт как «система не понимает сумм».
        { ValueKind: JsonValueKind.String } value => Parse(label ?? key, value.GetString()),
        var other => throw Wrong(label ?? key, other, "число"),
    };

    internal static int? Days(JsonElement source, string key) => Money(source, key) switch
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

    internal static JsonElement? Value(JsonElement source, string key) =>
        source.TryGetProperty(key, out var value) && value.ValueKind is not JsonValueKind.Null
            ? value
            : null;

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

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidRequestException(
                $"Поле «{key}»: «{text}» — не число. Разделителем дробной части понимается и точка, и " +
                "запятая, пробелы внутри числа не мешают; всё остальное разобрать нечем.");
    }
}
