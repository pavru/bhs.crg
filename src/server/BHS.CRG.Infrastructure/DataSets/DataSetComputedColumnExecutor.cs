using System.Text;
using System.Text.Json;
using Jint;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Вычисляемые колонки источника: JS-выражение на каждую строку, результат ложится новой колонкой.
///
/// Колонка доступна выражению ТРЕМЯ способами (issue #539) — потому что заголовок колонки вообще не
/// обязан быть допустимым JS-идентификатором, а у таблиц из распознавания PDF он сплошь и рядом
/// просто номер:
/// <list type="number">
/// <item><c>get("Кол-во")</c> — по точному заголовку. Основной способ, тот же, что у расчётных полей
///   (<c>JintExpressionEvaluator</c>, issue #368);</item>
/// <item><c>cols[1]</c> — по позиции, СЧЁТ С ЕДИНИЦЫ (нулевой элемент пуст, поэтому
///   <c>cols.length</c> на единицу больше числа колонок);</item>
/// <item>переменная с просанированным именем (<c>Кол_во</c>, <c>_1</c>) — как было до #539,
///   сохранено ради уже сохранённых выражений.</item>
/// </list>
///
/// Третий способ и был единственным, и этого не хватало: заголовок <c>1</c> превращался в переменную
/// <c>_1</c>, о которой пользователю негде было узнать, а естественное выражение <c>1</c> тихо
/// возвращало число вместо значения ячейки. Хуже, что разные заголовки схлопываются в одно имя
/// (<c>№</c> и <c>%</c> оба дают <c>_</c>) и затирают друг друга — вот там колонка терялась
/// по-настоящему. Затирание оставлено как есть (менять победителя — молча менять смысл сохранённых
/// выражений), но теперь из него есть выход через <c>get</c>.
/// </summary>
public static class DataSetComputedColumnExecutor
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static List<IReadOnlyDictionary<string, string?>> Apply(
        string? computedColumnsJson,
        List<IReadOnlyDictionary<string, string?>> rows)
    {
        if (string.IsNullOrWhiteSpace(computedColumnsJson)) return rows;

        ComputedColumnDef[]? defs;
        try { defs = JsonSerializer.Deserialize<ComputedColumnDef[]>(computedColumnsJson, JsonOpts); }
        catch { return rows; }

        if (defs == null || defs.Length == 0) return rows;

        foreach (var def in defs)
        {
            if (string.IsNullOrWhiteSpace(def.Alias) || string.IsNullOrWhiteSpace(def.Expr))
                continue;

            // Порядок колонок — ОДИН на весь прогон, а не по ключам каждой строки: строка, в которой
            // какой-то колонки не оказалось, сдвинула бы у себя все последующие позиции, и cols[3]
            // означал бы в разных строках разное. Считаем заново на каждую вычисляемую колонку —
            // так предыдущий alias виден следующему выражению и по позиции тоже.
            var columnOrder = ColumnOrder(rows);

            rows = rows.Select(row =>
            {
                var dict = new Dictionary<string, string?>(row);

                try
                {
                    var engine = new Engine(cfg => cfg
                        .TimeoutInterval(TimeSpan.FromSeconds(1))
                        .LimitRecursion(32));

                    // Помощники — ПЕРЕД колонками, чтобы колонка с именем «get» или «cols» затеняла
                    // их, а не наоборот. Порядок тут не косметика: до #539 такая колонка была
                    // обычной переменной, и обратный порядок молча поменял бы смысл уже сохранённых
                    // выражений. Ценой того, что на источнике с колонкой «get» помощник недоступен —
                    // но там он и не нужен, имя колонки и так годится в переменные.
                    //
                    // Отсутствующая колонка и колонка с пустым значением — разные вещи: первая даёт
                    // null (можно проверить), вторая — пустую строку, как и переменная.
                    engine.SetValue("get", new Func<string, string?>(
                        name => dict.TryGetValue(name, out var v) ? v ?? "" : null));

                    engine.SetValue("cols", PositionalCells(columnOrder, dict));

                    foreach (var (col, val) in dict)
                        engine.SetValue(SanitizeKey(col), val ?? "");

                    var result = engine.Evaluate(def.Expr);
                    dict[def.Alias] = result.IsNull() || result.IsUndefined()
                        ? null
                        : result.ToString();
                }
                catch
                {
                    dict[def.Alias] = null;
                }

                return (IReadOnlyDictionary<string, string?>)dict;
            }).ToList();
        }

        return rows;
    }

    /// <summary>
    /// Заголовки в порядке первого появления — та же логика, что у <c>ColumnsFromRows</c> в
    /// <c>DataSetSourceService</c>: колонки берутся объединением по всем строкам, потому что строки
    /// бывают рваные.
    /// </summary>
    static List<string> ColumnOrder(IEnumerable<IReadOnlyDictionary<string, string?>> rows)
    {
        var order = new List<string>();
        var seen = new HashSet<string>();
        foreach (var row in rows)
            foreach (var key in row.Keys)
                if (seen.Add(key)) order.Add(key);
        return order;
    }

    /// <summary>
    /// Значения строки по позициям колонок, со сдвигом на единицу: <c>cols[1]</c> — первая колонка
    /// (решение пользователя, issue #539). Jint отдаёт CLR-массив настоящим JS-массивом, так что
    /// <c>cols.slice(1).join('|')</c> в выражении работает.
    /// </summary>
    static string?[] PositionalCells(List<string> columnOrder, Dictionary<string, string?> row)
    {
        var cells = new string?[columnOrder.Count + 1];
        cells[0] = null;
        for (var i = 0; i < columnOrder.Count; i++)
            cells[i + 1] = row.GetValueOrDefault(columnOrder[i]) ?? "";
        return cells;
    }

    // Column names with spaces/special chars become underscores so they are valid JS identifiers.
    // E.g. "Полное Имя" → "Полное_Имя". Cyrillic letters are valid JS identifiers natively.
    static string SanitizeKey(string s)
    {
        if (string.IsNullOrEmpty(s)) return "col";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        var result = sb.ToString();
        return char.IsDigit(result[0]) ? "_" + result : result;
    }
}
