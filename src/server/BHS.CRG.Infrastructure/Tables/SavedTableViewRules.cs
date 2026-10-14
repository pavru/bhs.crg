using System.Text;
using System.Text.Json;
using BHS.CRG.Application.Tables;
using BHS.CRG.Domain.Tables;
using BHS.CRG.Infrastructure.DataSets;

namespace BHS.CRG.Infrastructure.Tables;

/// <summary>
/// Форма сохранённого представления таблицы (ТЗ CORE-33; задача G3a, issue #1095): что принимается
/// на запись и как состояние лежит в базе. Одно место на два входа — адрес сохранения и
/// восстановление из копии: копия приходит от другой сборки, и её состояние проверяется так же.
///
/// <para><b>Колонки с таблицей НЕ сверяются.</b> Поле схемы заказчик вправе убрать, и представление
/// обязано это пережить: колонка тогда приходит с причиной «поле удалено из типа», а отбор по ней
/// отказывает при открытии — названной колонкой, а не при сохранении чужой правкой типа.</para>
/// </summary>
public static class SavedTableViewRules
{
    /// <summary>Предел размера состояния: дерево условий с сотней поставщиков — около 10 КБ.</summary>
    public const int StateMaxBytes = 32 * 1024;

    public const int ColumnsMax = 200;
    public const int SortMax = 16;

    /// <summary>Сколько колонок закрепляется, когда представление показывает «все колонки таблицы».</summary>
    public const int PinnedMaxWithoutColumns = 50;

    /// <summary>
    /// Слова итогов. Второе место, где они записаны (первое — <c>ModuleTableView.Aggregates</c> в
    /// контрактах модулей, на которые этот проект не ссылается); равенство списков держит тест.
    /// </summary>
    public static readonly IReadOnlyList<string> Aggregates = ["sum", "avg", "min", "max", "count"];

    // Кириллица — как есть, а не шестью знаками на букву: иначе предел размера считался бы по записи
    // в шесть раз длиннее самой настройки, а «до» и «после» в журнале действий нельзя было бы прочесть.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Название как оно ляжет в базу и что с ним не так; <c>Problem</c> null — годится.</summary>
    public static (string Title, string? Problem) Title(string? title)
    {
        var clean = (title ?? "").Trim();
        if (clean.Length == 0) return (clean, "представлению нужно название");
        return clean.Length > SavedTableView.TitleMax
            ? (clean, $"название длиннее {SavedTableView.TitleMax} знаков")
            : (clean, null);
    }

    /// <summary>
    /// Состояние в хранимом виде и что с ним не так; <c>Problem</c> null — годится. Приводит к одному
    /// виду: пробелы у ключей сняты, пустой отбор — null, пустые списки — пустые, а не null.
    /// </summary>
    public static (TableViewState State, string? Problem) Normalize(TableViewState? raw)
    {
        raw ??= new TableViewState();

        var columns = raw.Columns?.Select(c => (c ?? "").Trim()).ToList();
        if (columns is not null)
        {
            if (columns.Count == 0) return (raw, "не названо ни одной колонки");
            if (columns.Any(c => c.Length == 0)) return (raw, "в списке колонок пустое место");
            if (columns.Count > ColumnsMax) return (raw, $"колонок больше {ColumnsMax}");
            if (Twice(columns) is { } twice) return (raw, $"колонка «{twice}» названа дважды");
        }

        var sort = (raw.Sort ?? []).Select(s => new TableSortRequest((s?.Column ?? "").Trim(), s?.Descending ?? false)).ToList();
        if (sort.Any(s => s.Column.Length == 0)) return (raw, "в сортировке пустое место");
        if (sort.Count > SortMax) return (raw, $"ключей сортировки больше {SortMax}");
        if (Twice(sort.Select(s => s.Column)) is { } sortedTwice)
            return (raw, $"сортировка по колонке «{sortedTwice}» названа дважды");

        var totals = (raw.Totals ?? [])
            .Select(t => new TableViewTotalDto((t?.Column ?? "").Trim(), (t?.Aggregate ?? "").Trim())).ToList();
        foreach (var total in totals)
        {
            if (total.Column.Length == 0) return (raw, "в итогах пустое место");
            if (!Aggregates.Contains(total.Aggregate, StringComparer.Ordinal))
                return (raw, $"итог «{total.Aggregate}» под колонкой «{total.Column}» не считается: бывают " +
                             string.Join(", ", Aggregates));
            // Итог под колонкой, которой в представлении нет, показать негде, а считать сервер
            // продолжал бы на каждую страницу.
            if (columns is not null && !columns.Contains(total.Column, StringComparer.Ordinal))
                return (raw, $"итог под колонкой «{total.Column}», которой в представлении нет");
        }
        if (Twice(totals.Select(t => t.Column)) is { } totalTwice)
            return (raw, $"под колонкой «{totalTwice}» два итога");

        var pinnedMax = columns?.Count ?? PinnedMaxWithoutColumns;
        if (raw.Pinned < 0 || raw.Pinned > pinnedMax)
            return (raw, $"закреплено колонок — {raw.Pinned}, а можно от 0 до {pinnedMax}");

        // Отбор разбирается тем же разбором, что при чтении строк: негодное дерево отказало бы при
        // первом же открытии — уже всем читателям общего представления.
        var filter = string.IsNullOrWhiteSpace(raw.Filter) ? null : raw.Filter.Trim();
        if (DataSetRowFilterExecutor.Problem(filter) is { } broken) return (raw, $"отбор не разобран: {broken}");

        var state = new TableViewState(columns, filter, sort, totals, raw.Pinned);
        return Encoding.UTF8.GetByteCount(Store(state)) > StateMaxBytes
            ? (state, $"настройка больше {StateMaxBytes / 1024} КБ — обычно это отбор с очень длинным перечнем значений")
            : (state, null);
    }

    public static string Store(TableViewState state) => JsonSerializer.Serialize(state, Json);

    /// <summary>
    /// Состояние из базы. Строка, которая не разбирается, — не пустое представление, а отказ: открыть
    /// таблицу «как по умолчанию» под именем чужой настройки значило бы показать не то, что сохранено.
    /// </summary>
    public static TableViewState Read(string stored) =>
        JsonSerializer.Deserialize<TableViewState>(stored, Json)
        ?? throw new JsonException("Состояние сохранённого представления записано значением «null».");

    private static string? Twice(IEnumerable<string> keys) =>
        keys.GroupBy(k => k, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key;
}
