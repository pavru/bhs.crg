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
    /// <para>Сверка — про правки, разделённые минутами: человек открыл диалог, и, пока он думал,
    /// источник поправил другой. Блокировки между сверкой и записью нет: два сохранения в одну и ту
    /// же долю секунды пройдут оба, и победит последнее.</para>
    /// </summary>
    public static bool Moved(DataSetSource source, string? seenVersion) =>
        seenVersion is not null && !string.Equals(seenVersion, Of(source), StringComparison.Ordinal);

    public static string Of(DataSetSource source) => Of(
        source.SheetOrPath, source.ColumnExpressions, source.RowFilter, source.ComputedColumns, source.SortSpec);

    public static string Of(
        string sheetOrPath, string? columnExpressions, string? rowFilter, string? computedColumns, string? sortSpec)
    {
        var sb = new StringBuilder(sheetOrPath);
        foreach (var json in (string?[])[columnExpressions, rowFilter, computedColumns, sortSpec])
            sb.Append(UnitSeparator).Append(Canonical(json));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        // Половины хеша хватает, как у отпечатка строк: это защита от «не заметили чужую правку»,
        // а не от подделки.
        return Convert.ToHexStringLower(hash)[..16];
    }

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
                // «1e2» база вернёт как «100»: число пишем значением. Не влезло в decimal — как есть:
                // таких чисел в обработке не бывает, а отказать в чтении источника из-за отпечатка
                // было бы хуже, чем изредка счесть одинаковое разным.
                writer.WriteRawValue(value.TryGetDecimal(out var number)
                    ? number.ToString("G29", CultureInfo.InvariantCulture)
                    : value.GetRawText());
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
}
