using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

/// <summary>
/// Переиспользуемый рецепт источника (Extraction + Filter/Transformation/Sort) — не привязан
/// к типу документа (в отличие от <see cref="DataSetBindingTemplate"/>, который про Mapping).
/// Применение к DataSetSource копирует значения единожды (copy-on-apply, как и шаблон
/// маппинга) — правка шаблона на уже применившие его источники не влияет.
/// </summary>
public class DataSetProcessingTemplate : Entity
{
    public string Name { get; private set; } = null!;

    /// <summary>
    /// Extraction (опционально): row-selector (XPath/JSONPath/имя листа — формат-зависимо) и
    /// JSON-массив явных относительных колонок, как на DataSetSource. Null/пусто — шаблон не
    /// переопределяет Extraction при применении, копируется только обработка ниже.
    /// </summary>
    public string? SheetOrPath { get; private set; }
    public string? ColumnExpressions { get; private set; }

    /// <summary>JSON: FilterDef { logic, conditions[] }. null = без фильтрации.</summary>
    public string? RowFilter { get; private set; }

    /// <summary>JSON: ComputedColumnDef[] { alias, expr }. null = нет вычисляемых колонок.</summary>
    public string? ComputedColumns { get; private set; }

    /// <summary>JSON: SortColumnDef[] { column, direction }. null = без сортировки.</summary>
    public string? SortSpec { get; private set; }

    private DataSetProcessingTemplate() { }

    public static DataSetProcessingTemplate Create(
        string name, string? sheetOrPath, string? columnExpressions,
        string? rowFilter, string? computedColumns, string? sortSpec)
        => new()
        {
            Name = name.Trim(),
            SheetOrPath = sheetOrPath,
            ColumnExpressions = columnExpressions,
            RowFilter = rowFilter,
            ComputedColumns = computedColumns,
            SortSpec = sortSpec,
        };

    public void Update(string name, string? sheetOrPath, string? columnExpressions,
        string? rowFilter, string? computedColumns, string? sortSpec)
    {
        Name = name.Trim();
        SheetOrPath = sheetOrPath;
        ColumnExpressions = columnExpressions;
        RowFilter = rowFilter;
        ComputedColumns = computedColumns;
        SortSpec = sortSpec;
        TouchUpdatedAt();
    }
}
