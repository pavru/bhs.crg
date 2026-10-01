using BHS.CRG.Application.DataSets;
using BHS.CRG.Application.Schema;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Состав колонок предпросмотра привязки (задача G1a этапа 2, issue #1088).
///
/// <para><b>Колонки — это размеченные поля в порядке схемы типа строки.</b> Раньше клиент брал их из
/// первой строки, и это было неверно дважды: у union строка несёт ключ одного варианта, и колонки
/// остальных вариантов пропадали; а пустое и отсутствующее значение становились неотличимы.</para>
///
/// <para><b>Поле, которого в типе нет, остаётся колонкой — с пометкой <c>removed</c></b>, а не
/// пропадает молча: маппинг на удалённое поле — ровно то, ради чего человек открывает предпросмотр.
/// Тип строки неизвестен — пометок нет вовсе: «не знаем, есть ли поле» не равно «поля нет».</para>
/// </summary>
public static class BindingPreviewColumns
{
    public const string Removed = "removed";

    /// <param name="mappedKeys">Поля, которым маппинг назначил колонку источника, в порядке маппинга.</param>
    /// <param name="fields">Эффективные поля типа строки; null — тип вывести не удалось.</param>
    public static IReadOnlyList<BindingPreviewColumnDto> Build(
        IEnumerable<string> mappedKeys, IReadOnlyList<SchemaFieldInfo>? fields)
    {
        var keys = mappedKeys.Distinct(StringComparer.Ordinal).ToList();
        if (fields is null)
            return keys.Select(k => new BindingPreviewColumnDto(k, k)).ToList();

        var known = fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        return fields
            .Where(f => keys.Contains(f.Key))
            .Select(f => new BindingPreviewColumnDto(f.Key, string.IsNullOrWhiteSpace(f.Title) ? f.Key : f.Title))
            .Concat(keys.Where(k => !known.Contains(k)).Select(k => new BindingPreviewColumnDto(k, k, Removed)))
            .ToList();
    }
}
