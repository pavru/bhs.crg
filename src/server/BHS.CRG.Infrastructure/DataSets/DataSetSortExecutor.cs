using System.Globalization;
using System.Text.Json;

namespace BHS.CRG.Infrastructure.DataSets;

public class SortColumnDef
{
    public string Column { get; set; } = "";
    /// <summary>"asc" | "desc"</summary>
    public string Direction { get; set; } = "asc";
}

/// <summary>
/// Сортировка строк по одной или нескольким колонкам (включая вычисляемые — применяется
/// после ComputedColumns). Числовое сравнение, если оба значения парсятся как числа,
/// иначе — строковое (culture-invariant, без учёта регистра). Null/пусто — всегда в конце,
/// независимо от направления.
/// </summary>
public static class DataSetSortExecutor
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static List<IReadOnlyDictionary<string, string?>> Apply(
        string? sortSpecJson,
        List<IReadOnlyDictionary<string, string?>> rows,
        string? sourceName = null)
    {
        if (string.IsNullOrWhiteSpace(sortSpecJson)) return rows;

        SortColumnDef[]? spec;
        try { spec = JsonSerializer.Deserialize<SortColumnDef[]>(sortSpecJson, JsonOpts); }
        catch (JsonException ex)
        {
            // Тот же род отказа, что у отбора (issue #966): прежде испорченное описание молча
            // возвращало строки как есть. Для сортировки это не лишние строки, а произвольный
            // порядок — и в кабельном журнале он выглядит как порядок, заданный настройкой.
            throw Refuse(sourceName, "описание порядка не разбирается.", ex);
        }

        // Ступень без колонки — половина заполненной настройки, а не испорченное описание: такую
        // пропускаем, как и прежде.
        var levels = (spec ?? []).Where(s => !string.IsNullOrWhiteSpace(s.Column)).ToArray();
        if (levels.Length == 0) return rows;

        // Направление проверяется ДО сортировки и целиком (ревью PR #1058). Прежде «descending»,
        // «убыв» и любая опечатка молча давали ВОЗРАСТАНИЕ: сравнение с «desc» не совпало — значит
        // по возрастанию. Это та же дверь, что «неизвестный оператор — истина» у отбора: негодная
        // настройка выглядела выполненной, а журнал, отсортированный не в ту сторону, спорит с
        // экраном только у того, кто помнит, чего просил. Пустое направление законно: это «asc» по
        // умолчанию (значение свойства), а не чей-то выбор.
        for (var i = 0; i < levels.Length; i++)
        {
            var dir = levels[i].Direction;
            if (string.IsNullOrWhiteSpace(dir)
                || string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase))
                continue;
            throw Refuse(sourceName,
                $"ступень {i + 1} по колонке «{levels[i].Column}» задана направлением «{dir}», "
                + "которого нет: бывают «asc» и «desc».");
        }

        // Направление кодируем в самом компараторе (а не через OrderByDescending — тот
        // инвертирует интерпретацию компаратора целиком, из-за чего null/пустые значения
        // при desc уехали бы в начало вместо конца). Поэтому всегда OrderBy/ThenBy.
        IOrderedEnumerable<IReadOnlyDictionary<string, string?>>? ordered = null;
        foreach (var level in levels)
        {
            var desc = string.Equals(level.Direction, "desc", StringComparison.OrdinalIgnoreCase);
            var comparer = new ValueComparer(desc);
            ordered = ordered is null
                ? rows.OrderBy(r => Key(r, level.Column), comparer)
                : ordered.ThenBy(r => Key(r, level.Column), comparer);
        }

        return ordered!.ToList();
    }

    static ConflictException Refuse(string? sourceName, string what, Exception? inner = null)
    {
        var named = string.IsNullOrWhiteSpace(sourceName) ? "" : $"«{sourceName}» ";
        return new ConflictException(
            $"Сортировка источника {named}не применена: {what} Строки не отданы вовсе — порядок, "
            + "которого не просили, выглядит как заданный. Исправьте сортировку.", inner);
    }

    private static string? Key(IReadOnlyDictionary<string, string?> row, string column)
        => row.TryGetValue(column, out var v) ? v : null;

    private class ValueComparer(bool desc) : IComparer<string?>
    {
        public int Compare(string? a, string? b)
        {
            // Null/пусто — всегда в конце, независимо от направления.
            var aEmpty = string.IsNullOrEmpty(a);
            var bEmpty = string.IsNullOrEmpty(b);
            if (aEmpty && bEmpty) return 0;
            if (aEmpty) return 1;
            if (bEmpty) return -1;

            int cmp;
            if (double.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out var da) &&
                double.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out var db))
                cmp = da.CompareTo(db);
            else
                cmp = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

            return desc ? -cmp : cmp;
        }
    }
}
