using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

/// <summary>
/// Переиспользуемый рецепт обработки набора данных (Filter/Conversion/Sort) — не привязан
/// к типу документа (в отличие от <see cref="DataSetBindingTemplate"/>, который про Mapping).
/// DataSetSource ссылается на шаблон живой связью (Guid): правка шаблона сразу отражается
/// на всех источниках, которые его используют.
/// </summary>
public class DataSetProcessingTemplate : Entity
{
    public string Name { get; private set; } = null!;

    /// <summary>JSON: FilterDef { logic, conditions[] }. null = без фильтрации.</summary>
    public string? RowFilter { get; private set; }

    /// <summary>JSON: ComputedColumnDef[] { alias, expr }. null = нет вычисляемых колонок.</summary>
    public string? ComputedColumns { get; private set; }

    /// <summary>JSON: SortColumnDef[] { column, direction }. null = без сортировки.</summary>
    public string? SortSpec { get; private set; }

    private DataSetProcessingTemplate() { }

    public static DataSetProcessingTemplate Create(
        string name, string? rowFilter, string? computedColumns, string? sortSpec)
        => new()
        {
            Name = name.Trim(),
            RowFilter = rowFilter,
            ComputedColumns = computedColumns,
            SortSpec = sortSpec,
        };

    public void Update(string name, string? rowFilter, string? computedColumns, string? sortSpec)
    {
        Name = name.Trim();
        RowFilter = rowFilter;
        ComputedColumns = computedColumns;
        SortSpec = sortSpec;
        TouchUpdatedAt();
    }
}
