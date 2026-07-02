using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using JsonCons.JsonPath;
using System.Text.Json;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// row-selector — JSONPath (JsonCons, https://danielaparker.github.io/JsonCons.Net) над корнем документа.
/// Авто-детект (DetectSourcesAsync) по-прежнему создаёт удобные источники на верхнем уровне
/// ("$root" / "$.имяСвойства") — они остаются рабочими, т.к. если селектор даёт РОВНО одно
/// совпадение и это массив, его элементы разворачиваются в строки (иначе каждое совпадение —
/// отдельная строка, что покрывает и "$.prop[*]", и фильтры/рекурсивный спуск "$..").
/// Колонки — либо явный список относительных JSONPath-выражений (builder), либо авто-определение
/// по ключам объекта-строки; для строк-скаляров (после фильтра/индекса) — одна колонка "value".
/// </summary>
public class JsonDataSetParser : IDataSetParser
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private record ColumnExprDef(string Name, string Expr);

    public bool CanParse(DataSetFormat format) => format is DataSetFormat.Json;

    public Task<IReadOnlyList<DataSetSourceInfo>> DetectSourcesAsync(byte[] bytes, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(bytes);
        var sources = new List<DataSetSourceInfo>();

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            var items = doc.RootElement.EnumerateArray().ToList();
            var cols = GetColumnsFromArray(items);
            sources.Add(new DataSetSourceInfo("root", "$root", cols, items.Count));
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    var items = prop.Value.EnumerateArray().ToList();
                    var cols = GetColumnsFromArray(items);
                    sources.Add(new DataSetSourceInfo(prop.Name, $"$.{prop.Name}", cols, items.Count));
                }
                else if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var cols = prop.Value.EnumerateObject()
                        .Select(p => new DataSetColumnInfo(p.Name, [JsonElementToString(p.Value) ?? ""]))
                        .ToArray();
                    sources.Add(new DataSetSourceInfo(prop.Name, $"$.{prop.Name}", cols, 1));
                }
            }

            // Fallback: root object as scalar
            if (sources.Count == 0)
            {
                var cols = doc.RootElement.EnumerateObject()
                    .Select(p => new DataSetColumnInfo(p.Name, [JsonElementToString(p.Value) ?? ""]))
                    .ToArray();
                sources.Add(new DataSetSourceInfo("root", "$root", cols, 1));
            }
        }

        return Task.FromResult<IReadOnlyList<DataSetSourceInfo>>(sources);
    }

    public Task<DataSetParseResult> ParseAsync(byte[] bytes, string sheetOrPath, string? columnExpressions, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(bytes);

        var path = sheetOrPath == "$root" ? "$" : sheetOrPath;
        var matches = JsonSelector.Parse(path).Select(doc.RootElement);

        // Единственное совпадение-массив — разворачиваем в строки (совместимость с "$root"/"$.prop"
        // для массива и удобство: "выбери массив — получи его строки"). Иначе каждое совпадение —
        // отдельная строка (покрывает "$.prop[*]", фильтры, рекурсивный спуск).
        var rows = matches.Count == 1 && matches[0].ValueKind == JsonValueKind.Array
            ? matches[0].EnumerateArray().ToList()
            : matches.ToList();

        if (rows.Count == 0) return Task.FromResult(new DataSetParseResult([], []));

        var defs = ParseColumnExpressions(columnExpressions);
        return Task.FromResult(defs is null
            ? ParseWithAutoColumns(rows)
            : ParseWithExplicitColumns(rows, defs));
    }

    // ── Явные относительные колонки (builder) ───────────────────────────────────

    private static DataSetParseResult ParseWithExplicitColumns(List<JsonElement> rows, ColumnExprDef[] defs)
    {
        const int sampleCount = 3;
        var columns = defs.Select(d => new DataSetColumnInfo(d.Name,
            rows.Take(sampleCount).Select(r => EvalExpr(r, d.Expr) ?? "").ToArray()
        )).ToArray();

        var resultRows = rows.Select(row =>
        {
            var dict = new Dictionary<string, string?>();
            foreach (var d in defs)
                dict[d.Name] = EvalExpr(row, d.Expr);
            return (IReadOnlyDictionary<string, string?>)dict;
        }).ToList();

        return new DataSetParseResult(columns, resultRows);
    }

    // expr без ведущего "$" считается относительным ("author" / "address.city") и дополняется
    // до "$.author" — вычисляется относительно текущей строки-JsonElement как корня.
    // Невозможность вычислить выражение для конкретной строки не должна ронять весь набор —
    // колонка просто остаётся пустой для этой строки (см. симметричный EvalExpr в XmlDataSetParser).
    private static string? EvalExpr(JsonElement row, string expr)
    {
        try
        {
            var normalized = expr.TrimStart().StartsWith('$') ? expr : $"$.{expr}";
            var matches = JsonSelector.Parse(normalized).Select(row);
            return matches.Count > 0 ? JsonElementToString(matches[0]) : null;
        }
        catch { return null; }
    }

    private static ColumnExprDef[]? ParseColumnExpressions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var defs = JsonSerializer.Deserialize<ColumnExprDef[]>(json, JsonOpts);
            return defs is { Length: > 0 } ? defs : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Авто-определение колонок (когда явные не заданы) ────────────────────────

    private static DataSetParseResult ParseWithAutoColumns(List<JsonElement> rows)
    {
        const int sampleCount = 3;

        // Строки-скаляры (после фильтра/индекса результат — не объект) — единственная колонка.
        if (rows.All(r => r.ValueKind != JsonValueKind.Object))
        {
            var col = new DataSetColumnInfo("value", rows.Take(sampleCount).Select(r => JsonElementToString(r) ?? "").ToArray());
            var scalarRows = rows.Select(r =>
                (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?> { ["value"] = JsonElementToString(r) }
            ).ToList();
            return new DataSetParseResult([col], scalarRows);
        }

        var allKeys = rows.SelectMany(GetRowKeys).Distinct().ToList();
        var columns = allKeys.Select(k => new DataSetColumnInfo(k,
            rows.Take(sampleCount).Select(r => GetRowValue(r, k) ?? "").ToArray()
        )).ToArray();

        var dictRows = rows.Select(row =>
        {
            var dict = new Dictionary<string, string?>();
            foreach (var key in allKeys)
                dict[key] = GetRowValue(row, key);
            return (IReadOnlyDictionary<string, string?>)dict;
        }).ToList();

        return new DataSetParseResult(columns, dictRows);
    }

    private static IEnumerable<string> GetRowKeys(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) yield break;
        foreach (var prop in row.EnumerateObject())
            yield return prop.Name;
    }

    private static string? GetRowValue(JsonElement row, string key)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var v) ? JsonElementToString(v) : null;

    private static DataSetColumnInfo[] GetColumnsFromArray(List<JsonElement> items)
    {
        const int sampleCount = 3;
        var allKeys = items
            .Where(e => e.ValueKind == JsonValueKind.Object)
            .SelectMany(e => e.EnumerateObject().Select(p => p.Name))
            .Distinct()
            .ToList();

        return allKeys.Select(k => new DataSetColumnInfo(k,
            items.Take(sampleCount)
                .Select(e => e.TryGetProperty(k, out var v) ? JsonElementToString(v) ?? "" : "")
                .ToArray()
        )).ToArray();
    }

    private static string? JsonElementToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.ToString(),
        JsonValueKind.True   => "true",
        JsonValueKind.False  => "false",
        JsonValueKind.Null   => null,
        _                    => el.ToString(), // object/array — сырой JSON-текст
    };
}
