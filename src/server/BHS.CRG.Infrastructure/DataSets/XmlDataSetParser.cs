using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.DataSets;
using System.Xml;

namespace BHS.CRG.Infrastructure.DataSets;

public class XmlDataSetParser : IDataSetParser
{
    public bool CanParse(DataSetFormat format) => format is DataSetFormat.Xml;

    public Task<IReadOnlyList<DataSetSourceInfo>> DetectSourcesAsync(byte[] bytes, CancellationToken ct)
    {
        var doc = LoadXml(bytes);
        var root = doc.DocumentElement;
        if (root == null) return Task.FromResult<IReadOnlyList<DataSetSourceInfo>>([]);

        var sources = new List<DataSetSourceInfo>();

        // Group direct children by element name
        var childGroups = root.ChildNodes
            .Cast<XmlNode>()
            .Where(n => n.NodeType == XmlNodeType.Element)
            .GroupBy(n => n.Name)
            .ToList();

        if (childGroups.Count == 0)
        {
            // Root itself is the single record
            var cols = GetColumnsFromNodes([root]);
            sources.Add(new DataSetSourceInfo(root.Name, $"/{root.Name}", cols, 1));
        }
        else
        {
            foreach (var group in childGroups)
            {
                var nodes = group.Cast<XmlNode>().ToList();
                var path = $"/{root.Name}/{group.Key}";
                var cols = GetColumnsFromNodes(nodes);
                sources.Add(new DataSetSourceInfo(group.Key, path, cols, nodes.Count));
            }
        }

        return Task.FromResult<IReadOnlyList<DataSetSourceInfo>>(sources);
    }

    public Task<DataSetParseResult> ParseAsync(byte[] bytes, string sheetOrPath, CancellationToken ct)
    {
        var doc = LoadXml(bytes);
        var nodes = doc.SelectNodes(sheetOrPath)?.Cast<XmlNode>().ToList() ?? [];
        if (nodes.Count == 0) return Task.FromResult(new DataSetParseResult([], []));

        // Collect all keys (child element names + attributes)
        var allKeys = nodes
            .SelectMany(n => GetNodeKeys(n))
            .Distinct()
            .ToList();

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

        return Task.FromResult(new DataSetParseResult(columns, rows));
    }

    private static XmlDocument LoadXml(byte[] bytes)
    {
        var doc = new XmlDocument();
        doc.Load(new MemoryStream(bytes));
        return doc;
    }

    private static DataSetColumnInfo[] GetColumnsFromNodes(List<XmlNode> nodes)
    {
        const int sampleCount = 3;
        var allKeys = nodes.SelectMany(n => GetNodeKeys(n)).Distinct().ToList();
        return allKeys.Select(k => new DataSetColumnInfo(k,
            nodes.Take(sampleCount).Select(n => GetNodeValue(n, k) ?? "").ToArray()
        )).ToArray();
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
