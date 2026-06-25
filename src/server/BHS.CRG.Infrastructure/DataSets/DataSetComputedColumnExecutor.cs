using System.Text;
using System.Text.Json;
using Scriban;
using Scriban.Runtime;

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

            var tmpl = Template.Parse(def.Expr);
            if (tmpl.HasErrors) continue;

            rows = rows.Select(row =>
            {
                var dict = new Dictionary<string, string?>(row);
                var so = new ScriptObject();
                foreach (var (col, val) in dict)
                    so.SetValue(SanitizeKey(col), val ?? "", readOnly: true);
                var ctx = new TemplateContext { LoopLimit = 100, RecursiveLimit = 10 };
                ctx.PushGlobal(so);
                dict[def.Alias] = tmpl.Render(ctx);
                return (IReadOnlyDictionary<string, string?>)dict;
            }).ToList();
        }

        return rows;
    }

    // Converts column name to a valid Scriban identifier.
    // Replaces all non-letter/non-digit chars with underscore; prepends underscore if starts with digit.
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
