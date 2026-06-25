using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using System.Text.Json;

namespace BHS.CRG.Infrastructure.DataSets;

public class JsonDataSetParser : IDataSetParser
{
    public bool CanParse(DataSetFormat format) => format is DataSetFormat.Json;

    public Task<IReadOnlyList<DataSetSourceInfo>> DetectSourcesAsync(byte[] bytes, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(bytes);
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
                        .Select(p => new DataSetColumnInfo(p.Name, [p.Value.ToString()]))
                        .ToArray();
                    sources.Add(new DataSetSourceInfo(prop.Name, $"$.{prop.Name}", cols, 1));
                }
            }

            // Fallback: root object as scalar
            if (sources.Count == 0)
            {
                var cols = doc.RootElement.EnumerateObject()
                    .Select(p => new DataSetColumnInfo(p.Name, [p.Value.ToString()]))
                    .ToArray();
                sources.Add(new DataSetSourceInfo("root", "$root", cols, 1));
            }
        }

        return Task.FromResult<IReadOnlyList<DataSetSourceInfo>>(sources);
    }

    public Task<DataSetParseResult> ParseAsync(byte[] bytes, string sheetOrPath, CancellationToken ct)
    {
        var doc = JsonDocument.Parse(bytes);

        JsonElement target;
        if (sheetOrPath == "$root")
        {
            target = doc.RootElement;
        }
        else if (sheetOrPath.StartsWith("$."))
        {
            var key = sheetOrPath[2..];
            if (!doc.RootElement.TryGetProperty(key, out target))
                return Task.FromResult(new DataSetParseResult([], []));
        }
        else
        {
            target = doc.RootElement;
        }

        if (target.ValueKind == JsonValueKind.Array)
        {
            var items = target.EnumerateArray().ToList();
            var columns = GetColumnsFromArray(items);
            var rows = items.Select(FlattenObject).ToList<IReadOnlyDictionary<string, string?>>();
            return Task.FromResult(new DataSetParseResult(columns, rows));
        }

        if (target.ValueKind == JsonValueKind.Object)
        {
            var dict = FlattenObject(target);
            DataSetColumnInfo[] columns = [..dict.Keys.Select(k => new DataSetColumnInfo(k, [dict[k] ?? ""]))];
            return Task.FromResult(new DataSetParseResult(columns, [dict]));
        }

        return Task.FromResult(new DataSetParseResult([], []));
    }

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
                .Select(e => e.TryGetProperty(k, out var v) ? v.ToString() : "")
                .ToArray()
        )).ToArray();
    }

    private static IReadOnlyDictionary<string, string?> FlattenObject(JsonElement element)
    {
        var dict = new Dictionary<string, string?>();
        if (element.ValueKind != JsonValueKind.Object) return dict;
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.ToString(),
                JsonValueKind.True   => "true",
                JsonValueKind.False  => "false",
                JsonValueKind.Null   => null,
                _                    => prop.Value.ToString(),
            };
        }
        return dict;
    }
}
