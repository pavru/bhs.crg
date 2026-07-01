using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using System.Xml;

namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// XML — единственный формат без авто-детекта источников: структура произвольного XML
/// слишком разнообразна для надёжной эвристики (в отличие от строк CSV или листов Excel).
/// Источники создаются пользователем вручную через builder (row-selector XPath + опционально
/// список относительных колонок) — см. IDataSetService.CreateSourceAsync/UpdateSourceAsync.
/// </summary>
public class XmlDataSetParser : IDataSetParser
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private record ColumnExprDef(string Name, string Expr);

    public bool CanParse(DataSetFormat format) => format is DataSetFormat.Xml;

    public Task<IReadOnlyList<DataSetSourceInfo>> DetectSourcesAsync(byte[] bytes, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<DataSetSourceInfo>>([]);

    public Task<DataSetParseResult> ParseAsync(byte[] bytes, string sheetOrPath, string? columnExpressions, CancellationToken ct)
    {
        var doc = LoadXml(bytes);
        var nodes = doc.SelectNodes(sheetOrPath)?.Cast<XmlNode>().ToList() ?? [];
        if (nodes.Count == 0) return Task.FromResult(new DataSetParseResult([], []));

        var defs = ParseColumnExpressions(columnExpressions);
        return Task.FromResult(defs is null
            ? ParseWithAutoColumns(nodes)
            : ParseWithExplicitColumns(nodes, defs));
    }

    // ── Явные относительные колонки (builder) ───────────────────────────────────

    private static DataSetParseResult ParseWithExplicitColumns(List<XmlNode> nodes, ColumnExprDef[] defs)
    {
        const int sampleCount = 3;
        var columns = defs.Select(d => new DataSetColumnInfo(d.Name,
            nodes.Take(sampleCount).Select(n => EvalExpr(n, d.Expr) ?? "").ToArray()
        )).ToArray();

        var rows = nodes.Select(node =>
        {
            var dict = new Dictionary<string, string?>();
            foreach (var d in defs)
                dict[d.Name] = EvalExpr(node, d.Expr);
            return (IReadOnlyDictionary<string, string?>)dict;
        }).ToList();

        return new DataSetParseResult(columns, rows);
    }

    private static string? EvalExpr(XmlNode node, string expr)
        => node.SelectSingleNode(expr)?.InnerText;

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

    // ── Легаси авто-определение колонок (когда явные не заданы) ─────────────────

    private static DataSetParseResult ParseWithAutoColumns(List<XmlNode> nodes)
    {
        var allKeys = nodes.SelectMany(GetNodeKeys).Distinct().ToList();

        const int sampleCount = 3;
        var columns = allKeys.Select(k => new DataSetColumnInfo(k,
            nodes.Take(sampleCount).Select(n => GetNodeValue(n, k) ?? "").ToArray()
        )).ToArray();

        var rows = nodes.Select(node =>
        {
            var dict = new Dictionary<string, string?>();
            foreach (var key in allKeys)
                dict[key] = GetNodeValue(node, key);
            return (IReadOnlyDictionary<string, string?>)dict;
        }).ToList();

        return new DataSetParseResult(columns, rows);
    }

    private static XmlDocument LoadXml(byte[] bytes)
    {
        var doc = new XmlDocument();
        doc.Load(new MemoryStream(bytes));
        return doc;
    }

    private static IEnumerable<string> GetNodeKeys(XmlNode node)
    {
        if (node.Attributes != null)
            foreach (XmlAttribute attr in node.Attributes)
                yield return $"@{attr.Name}";

        foreach (XmlNode child in node.ChildNodes)
            if (child.NodeType == XmlNodeType.Element)
                yield return child.Name;
    }

    private static string? GetNodeValue(XmlNode node, string key)
    {
        if (key.StartsWith('@'))
            return node.Attributes?[key[1..]]?.Value;
        return node.SelectSingleNode(key)?.InnerText;
    }
}
