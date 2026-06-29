using System.Text;
using System.Text.Json;
using Jint;

namespace BHS.CRG.Infrastructure.DataSets;

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

            rows = rows.Select(row =>
            {
                var dict = new Dictionary<string, string?>(row);

                try
                {
                    var engine = new Engine(cfg => cfg
                        .TimeoutInterval(TimeSpan.FromSeconds(1))
                        .LimitRecursion(32));

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
