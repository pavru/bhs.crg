using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

public class DataSetSource : Entity
{
    public Guid FileId { get; private set; }
    /// <summary>Display name: sheet name, XML group name, JSON key, or "default".</summary>
    public string Name { get; private set; } = null!;
    /// <summary>Internal locator: sheet name, XPath (/root/items), JSON path ($.key), "default".</summary>
    public string SheetOrPath { get; private set; } = null!;
    /// <summary>JSON-кэш колонок: [{name, sampleValues[]}]. Заполняется при загрузке файла.</summary>
    public string CachedSchema { get; private set; } = "[]";
    public int CachedRowCount { get; private set; }

    public DataSetFile File { get; private set; } = null!;
    private readonly List<DataSetBinding> _bindings = [];
    public IReadOnlyList<DataSetBinding> Bindings => _bindings.AsReadOnly();

    private DataSetSource() { }

    internal static DataSetSource Create(Guid fileId, string name, string sheetOrPath,
        string cachedSchema, int cachedRowCount)
        => new()
        {
            FileId = fileId,
            Name = name,
            SheetOrPath = sheetOrPath,
            CachedSchema = cachedSchema,
            CachedRowCount = cachedRowCount,
        };

    public void UpdateCache(string cachedSchema, int cachedRowCount)
    {
        CachedSchema = cachedSchema;
        CachedRowCount = cachedRowCount;
        TouchUpdatedAt();
    }
}
