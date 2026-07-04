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
    /// <summary>
    /// JSON-массив полных распознанных строк (только для PDF — распознавание через vision-LLM
    /// дорого/недетерминированно, в отличие от остальных форматов не перепарсивается на каждый
    /// вызов). Null — ещё не распознавали. См. DataSetBindingProcessor.LoadRowsAsync.
    /// </summary>
    public string? CachedData { get; private set; }
    /// <summary>JSON-массив кодов функциональных тэгов источника (scope Dataset — TagRegistry).</summary>
    public string? Tags { get; private set; }

    /// <summary>
    /// Обработка (Filter/Transformation/Sort) — своя, независимая от других источников.
    /// Применение шаблона обработки (<see cref="DataSetProcessingTemplate"/>) копирует его
    /// значения сюда единожды (как и применение шаблона маппинга к DataSetBinding) — дальше
    /// правки шаблона на уже применившие его источники не влияют. JSON: FilterDef /
    /// ComputedColumnDef[] / SortColumnDef[] соответственно.
    /// </summary>
    public string? RowFilter { get; private set; }
    public string? ComputedColumns { get; private set; }
    public string? SortSpec { get; private set; }

    /// <summary>
    /// JSON-объект группировки страниц по документам для ГОСТ-профиля "Документы"
    /// ({"documents":[{"code","name","pageIndices"}],"manuallyEdited"}) — задел под ручную
    /// корректировку разбиения PDF после автораспознавания. Null — источник не из ГОСТ-профиля
    /// или ещё не распознавался. См. GostGroupingData/DataSetPdfRecognitionService.
    /// </summary>
    public string? GostGrouping { get; private set; }

    public DataSetFile File { get; private set; } = null!;
    private readonly List<DataSetBinding> _bindings = [];
    public IReadOnlyList<DataSetBinding> Bindings => _bindings.AsReadOnly();

    private DataSetSource() { }

    internal static DataSetSource Create(Guid fileId, string name, string sheetOrPath,
        string cachedSchema, int cachedRowCount, string? columnExpressions = null, string? cachedData = null)
        => new()
        {
            FileId = fileId,
            Name = name,
            SheetOrPath = sheetOrPath,
            CachedSchema = cachedSchema,
            CachedRowCount = cachedRowCount,
            ColumnExpressions = columnExpressions,
            CachedData = cachedData,
        };

    public void UpdateCache(string cachedSchema, int cachedRowCount, string? cachedData = null)
    {
        CachedSchema = cachedSchema;
        CachedRowCount = cachedRowCount;
        CachedData = cachedData;
        TouchUpdatedAt();
    }

    /// <summary>Функциональные тэги источника (scope Dataset) — JSON-массив кодов или null.</summary>
    public void SetTags(string? tagsJson)
    {
        Tags = tagsJson;
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

    /// <summary>Обработка (Filter/Transformation/Sort) — лёгкая правка, не трогает файл/кэш схемы.</summary>
    public void SetProcessing(string? rowFilter, string? computedColumns, string? sortSpec)
    {
        RowFilter = rowFilter;
        ComputedColumns = computedColumns;
        SortSpec = sortSpec;
        TouchUpdatedAt();
    }

    /// <summary>Группировка страниц ГОСТ-профиля — задаётся автораспознаванием (manuallyEdited=false)
    /// или ручной правкой пользователя (manuallyEdited=true).</summary>
    public void SetGostGrouping(string? gostGroupingJson)
    {
        GostGrouping = gostGroupingJson;
        TouchUpdatedAt();
    }
}
