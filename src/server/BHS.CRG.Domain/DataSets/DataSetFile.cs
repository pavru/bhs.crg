using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

public enum DataSetFormat { Csv, Xlsx, Xls, Xml, Json, Zip }

public class DataSetFile : Entity
{
    public string Name { get; private set; } = null!;
    public DataSetFormat Format { get; private set; }
    public string BlobPath { get; private set; } = null!;
    public CatalogScope Scope { get; private set; }
    public Guid? ScopeId { get; private set; }

    private readonly List<DataSetSource> _sources = [];
    public IReadOnlyList<DataSetSource> Sources => _sources.AsReadOnly();

    private DataSetFile() { }

    public static DataSetFile Create(string name, DataSetFormat format, string blobPath,
        CatalogScope scope, Guid? scopeId)
        => new() { Name = name, Format = format, BlobPath = blobPath, Scope = scope, ScopeId = scopeId };

    public DataSetSource AddSource(string name, string sheetOrPath, string cachedSchema, int cachedRowCount,
        string? columnExpressions = null)
    {
        var src = DataSetSource.Create(Id, name, sheetOrPath, cachedSchema, cachedRowCount, columnExpressions);
        _sources.Add(src);
        TouchUpdatedAt();
        return src;
    }

    public void ReplaceAllSources(IEnumerable<DataSetSource> sources)
    {
        _sources.Clear();
        _sources.AddRange(sources);
        TouchUpdatedAt();
    }

    public void UpdateName(string name) { Name = name.Trim(); TouchUpdatedAt(); }

    public void UpdateBlobPath(string newBlobPath, DataSetFormat newFormat)
    {
        BlobPath = newBlobPath;
        Format = newFormat;
        TouchUpdatedAt();
    }
}
