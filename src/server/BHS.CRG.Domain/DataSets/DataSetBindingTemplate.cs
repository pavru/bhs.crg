using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

/// <summary>
/// Шаблон стандартной привязки к набору данных для типа документа.
/// Не содержит ссылку на конкретный файл — только ожидаемые имена колонок.
/// При создании экземпляра маппинг копируется в DataSetBinding.
/// </summary>
public class DataSetBindingTemplate : Entity
{
    public Guid DocumentTypeId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>null = скалярный; строка = ключ array-поля (табличный).</summary>
    public string? TargetFieldKey { get; private set; }

    /// <summary>JSON: { "ключПоля": "ОжидаемаяКолонкаВФайле" }</summary>
    public string ColumnMappings { get; private set; } = "{}";

    /// <summary>JSON: FilterDef { logic, conditions[] }. null = без фильтрации.</summary>
    public string? RowFilter { get; private set; }

    /// <summary>JSON: ComputedColumnDef[] { alias, expr }. null = нет вычисляемых колонок.</summary>
    public string? ComputedColumns { get; private set; }

    public int SortOrder { get; private set; }

    private DataSetBindingTemplate() { }

    public static DataSetBindingTemplate Create(
        Guid documentTypeId, string name, string? targetFieldKey,
        string columnMappings, string? rowFilter, string? computedColumns, int sortOrder = 0)
        => new()
        {
            DocumentTypeId = documentTypeId,
            Name = name.Trim(),
            TargetFieldKey = targetFieldKey,
            ColumnMappings = columnMappings,
            RowFilter = rowFilter,
            ComputedColumns = computedColumns,
            SortOrder = sortOrder,
        };

    public void Update(string name, string? targetFieldKey, string columnMappings,
        string? rowFilter, string? computedColumns, int sortOrder)
    {
        Name = name.Trim();
        TargetFieldKey = targetFieldKey;
        ColumnMappings = columnMappings;
        RowFilter = rowFilter;
        ComputedColumns = computedColumns;
        SortOrder = sortOrder;
        TouchUpdatedAt();
    }
}
