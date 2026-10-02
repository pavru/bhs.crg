using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BHS.CRG.Domain.DataSets;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Версия обработки источника (issue #1141) — отпечаток того, из чего собраны диалоги обработки и из
/// чего состоит шаблон: извлечения (локатор, колонки) и трёх частей обработки. Страница получает её
/// с источником и называет при сохранении; не совпала с сохранённой — источник тем временем изменили,
/// и правка, собранная по прежней копии, затёрла бы чужую.
///
/// <para><b>Почему отпечаток содержимого, а не <c>UpdatedAt</c> и не <c>xmin</c>.</b> Оба двигаются
/// правками, к обработке не относящимися: обновлением кэша схемы, распознаванием, пометкой
/// «устарело», тэгами, переименованием, материализацией. Распознавание идёт в фоне минутами, и
/// человек, поправивший за это время сортировку, получал бы «источник изменили» про изменение,
/// которое его правке не мешает. Отпечаток меняется, только когда изменилось то, что человек видел
/// в диалоге, — и не требует ни колонки, ни миграции. Способ в проекте уже принят: так устроены
/// <c>pageHash</c> и <c>ifNoneMatch</c> у выдач MCP (<c>RowsFingerprint</c>).</para>
///
/// <para><b>Извлечение входит</b>, хотя правка обработки его не трогает: сменили лист или колонки —
/// диалог предлагает колонки, которых у источника уже нет. И шаблон обработки несёт извлечение
/// вместе с обработкой, так что «сохранить как шаблон» сверяется с тем же отпечатком.</para>
///
/// <para><b>Считается по значению, а не по тексту.</b> Части лежат в <c>jsonb</c>: база переставляет
/// ключи, меняет пробелы и запись чисел. Отпечаток по тексту у источника «только что сохранён» и у
/// него же «прочитан из базы» вышел бы разным — и следующее сохранение того же человека получило бы
/// отказ на собственную правку.</para>
/// </summary>
public static class SourceProcessingVersion
{
    /// <summary>Управляющий символ: в локаторе и в JSON без экранирования он не встречается, поэтому
    /// отбор, переехавший в соседнюю часть, не даст того же отпечатка.</summary>
    private const char UnitSeparator = '\u001f';

    /// <summary>
    /// Изменилась ли обработка с тех пор, как её видел правящий. Версии не назвали (<c>null</c>) —
    /// сверять не с чем: называть ли её, решает вход (см. <c>SetSourceProcessingInput.IfMatch</c>).
    ///
    /// <para>Сама по себе сверка не атомарна: между ней и записью источник может поправить другой
    /// запрос. Неразрывной её делает тот, кто пишет, — сверяя ещё раз под блокировкой строки
    /// (<c>DataSetSourceService.LockAsync</c>).</para>
    /// </summary>
    public static bool Moved(DataSetSource source, string? seenVersion) =>
        seenVersion is not null && !string.Equals(seenVersion, Of(source), StringComparison.Ordinal);

    /// <summary>То же для настройки материализации — сверяется с <see cref="OfMaterialization" />.</summary>
    public static bool MaterializationMoved(DataSetSource source, string? seenVersion) =>
        seenVersion is not null && !string.Equals(seenVersion, OfMaterialization(source), StringComparison.Ordinal);

    public static string Of(DataSetSource source) => Of(
        source.SheetOrPath, source.ColumnExpressions, source.RowFilter, source.ComputedColumns, source.SortSpec);

    /// <summary>
    /// Версия материализации: её настройка (тип, маппинг, правило варианта, колонка «по Ид») ПЛЮС всё,
    /// что входит в версию обработки. Диалог материализации сопоставляет поля типа с колонками
    /// источника, а колонки дают извлечение и вычисляемые колонки: сменили их за спиной страницы — и
    /// маппинг указывал бы на колонки, которых уже нет.
    ///
    /// <para>Отдельная версия, а не общая с обработкой: наоборот зависимости нет. Диалогу сортировки
    /// всё равно, во что источник материализуется, и общая версия отказывала бы ему на чужую правку
    /// материализации.</para>
    /// </summary>
    public static string OfMaterialization(DataSetSource source)
    {
        var sb = new StringBuilder(Of(source))
            .Append(UnitSeparator).Append(source.MaterializeTypeId)
            .Append(UnitSeparator).Append(Canonical(source.MaterializeMapping))
            .Append(UnitSeparator).Append(Canonical(source.MaterializeDiscriminator))
            .Append(UnitSeparator).Append(source.MaterializeByIdColumn);
        return Fingerprint(sb.ToString());
    }

    public static string Of(
        string sheetOrPath, string? columnExpressions, string? rowFilter, string? computedColumns, string? sortSpec)
    {
        var sb = new StringBuilder(sheetOrPath);
        foreach (var json in (string?[])[columnExpressions, rowFilter, computedColumns, sortSpec])
            sb.Append(UnitSeparator).Append(Canonical(json));
        return Fingerprint(sb.ToString());
    }

    /// <summary>Половины хеша хватает, как у отпечатка строк: это защита от «не заметили чужую
    /// правку», а не от подделки.</summary>
    private static string Fingerprint(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];

    /// <summary>Значение в записи, не зависящей от того, как его записали; части нет — пусто.</summary>
    private static string Canonical(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, document.RootElement);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Повтор ключа база сводит к последнему значению, порядок ключей — к своему.
                foreach (var property in value.EnumerateObject()
                             .GroupBy(p => p.Name, StringComparer.Ordinal).Select(g => g.Last())
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(Number(value.GetRawText()));
                break;
            case JsonValueKind.String:
                // Значением: одна и та же строка бывает записана с разным экранированием.
                writer.WriteStringValue(value.GetString());
                break;
            default:
                value.WriteTo(writer); // true, false, null
                break;
        }
    }

    /// <summary>
    /// Число значением: значащие цифры без нулей по краям и порядок. «1e2», «100» и «100.0» дают одну
    /// запись — и так для числа ЛЮБОЙ величины. Через <c>decimal</c> нельзя: база хранит числа
    /// <c>jsonb</c> точно и возвращает их развёрнутыми («1e30» — единицей с тридцатью нулями), а в
    /// <c>decimal</c> такое не влезает. Отпечаток по сырому тексту разошёлся бы с базой, и источник с
    /// таким числом отвечал бы «изменили» на каждую следующую правку.
    /// </summary>
    private static string Number(string raw)
    {
        var negative = raw.StartsWith('-');
        var body = negative ? raw[1..] : raw;
        var exponentAt = body.IndexOfAny(['e', 'E']);
        var exponent = 0L;
        // Порядок, не влезающий в long, базе не записать вовсе — такое число оставляем как есть.
        if (exponentAt >= 0 && !long.TryParse(
                body[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            return raw;

        var mantissa = exponentAt < 0 ? body : body[..exponentAt];
        var pointAt = mantissa.IndexOf('.');
        if (pointAt >= 0)
        {
            exponent -= mantissa.Length - pointAt - 1;
            mantissa = mantissa.Remove(pointAt, 1);
        }

        var digits = mantissa.TrimStart('0');
        var significant = digits.TrimEnd('0');
        if (significant.Length == 0) return "0";
        exponent += digits.Length - significant.Length;
        return $"{(negative ? "-" : "")}{significant}E{exponent}";
    }
}
