using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

public class DataSetSource : Entity
{
    public Guid FileId { get; private set; }
    /// <summary>Display name: sheet name, XML group name, JSON key, or "default".</summary>
    public string Name { get; private set; } = null!;
    /// <summary>Internal locator: sheet name, XPath (/root/items), JSON path ($.key), "default".</summary>
    public string SheetOrPath { get; private set; } = null!;
    /// <summary>
    /// Для XML (опционально): JSON-массив явных относительных колонок вида
    /// [{"name":"Артикул","expr":"@id"}]. Вычисляются относительно узла строки (SheetOrPath).
    /// Null/пусто — авто-определение колонок по дочерним элементам/атрибутам (легаси-режим).
    /// </summary>
    public string? ColumnExpressions { get; private set; }
    /// <summary>JSON-кэш колонок: [{name, sampleValues[]}]. Заполняется при загрузке файла.</summary>
    public string CachedSchema { get; private set; } = "[]";
    public int CachedRowCount { get; private set; }

    public DataSetFile File { get; private set; } = null!;
    private readonly List<DataSetBinding> _bindings = [];
    public IReadOnlyList<DataSetBinding> Bindings => _bindings.AsReadOnly();

    private DataSetSource() { }

    internal static DataSetSource Create(Guid fileId, string name, string sheetOrPath,
        string cachedSchema, int cachedRowCount, string? columnExpressions = null)
        => new()
        {
            FileId = fileId,
            Name = name,
            SheetOrPath = sheetOrPath,
            CachedSchema = cachedSchema,
            CachedRowCount = cachedRowCount,
            ColumnExpressions = columnExpressions,
        };

    public void UpdateCache(string cachedSchema, int cachedRowCount)
    {
        CachedSchema = cachedSchema;
        CachedRowCount = cachedRowCount;
        TouchUpdatedAt();
    }

    /// <summary>Ручное редактирование источника пользователем (имя, локатор, колонки).</summary>
    public void UpdateDefinition(string name, string sheetOrPath, string? columnExpressions)
    {
        Name = name;
        SheetOrPath = sheetOrPath;
        ColumnExpressions = columnExpressions;
        TouchUpdatedAt();
    }
}
