using System.Text.Json;

namespace BHS.CRG.Infrastructure.DataSets;

public static class DataSetRowFilterExecutor
{
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static List<IReadOnlyDictionary<string, string?>> Apply(
        string? rowFilterJson,
        List<IReadOnlyDictionary<string, string?>> rows)
    {
        if (string.IsNullOrWhiteSpace(rowFilterJson)) return rows;

        FilterNode? root;
        try { root = JsonSerializer.Deserialize<FilterNode>(rowFilterJson, JsonOpts); }
        catch { return rows; }

        if (root == null) return rows;

        return rows.Where(row => Evaluate(root, row)).ToList();
    }

    static bool Evaluate(FilterNode node, IReadOnlyDictionary<string, string?> row)
    {
        if (node.Type == "condition")
            return Match(node, row);

        // group
        var children = node.Children ?? [];
        if (children.Length == 0) return true;
        return node.Logic == "or"
            ? children.Any(c => Evaluate(c, row))
            : children.All(c => Evaluate(c, row));
    }

    static bool Match(FilterNode cond, IReadOnlyDictionary<string, string?> row)
    {
        var col = cond.Column ?? "";
        var val = row.TryGetValue(col, out var v) ? v ?? "" : "";
        var expected = cond.Value ?? "";

        return (cond.Op ?? "eq") switch
        {
            "eq"           => string.Equals(val, expected, StringComparison.OrdinalIgnoreCase),
            "neq"          => !string.Equals(val, expected, StringComparison.OrdinalIgnoreCase),
            "contains"     => val.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "not_contains" => !val.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "starts_with"  => val.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            "ends_with"    => val.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            "gt"           => CompareNumOrStr(val, expected) > 0,
            "gte"          => CompareNumOrStr(val, expected) >= 0,
            "lt"           => CompareNumOrStr(val, expected) < 0,
            "lte"          => CompareNumOrStr(val, expected) <= 0,
            "is_empty"     => string.IsNullOrEmpty(val),
            "is_not_empty" => !string.IsNullOrEmpty(val),
            _              => true,
        };
    }

    static int CompareNumOrStr(string a, string b)
    {
        if (double.TryParse(a, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var da) &&
            double.TryParse(b, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var db))
            return da.CompareTo(db);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
