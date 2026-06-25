using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

public class DataSetBinding : Entity
{
    public Guid InstanceId { get; private set; }
    public Guid SourceId { get; private set; }

    /// <summary>
    /// null — скалярный режим: первая строка → реквизиты по маппингу.
    /// Задано — табличный режим: все строки → array-поле с этим ключом.
    /// </summary>
    public string? TargetFieldKey { get; private set; }

    /// <summary>JSON: { "ключПоляДокумента": "НазваниеКолонки" }</summary>
    public string Mapping { get; private set; } = "{}";

    /// <summary>JSON: FilterDef { logic, conditions[] }. null = без фильтрации.</summary>
    public string? RowFilter { get; private set; }

    /// <summary>JSON: ComputedColumnDef[] { alias, expr }. null = нет вычисляемых колонок.</summary>
    public string? ComputedColumns { get; private set; }

    public DataSetSource Source { get; private set; } = null!;

    private DataSetBinding() { }

    public static DataSetBinding Create(
        Guid instanceId, Guid sourceId, string? targetFieldKey, string mapping,
        string? rowFilter = null, string? computedColumns = null)
        => new()
        {
            InstanceId = instanceId,
            SourceId = sourceId,
            TargetFieldKey = targetFieldKey,
            Mapping = mapping,
            RowFilter = rowFilter,
            ComputedColumns = computedColumns,
        };

    public void Update(string? targetFieldKey, string mapping, string? rowFilter, string? computedColumns)
    {
        TargetFieldKey = targetFieldKey;
        Mapping = mapping;
        RowFilter = rowFilter;
        ComputedColumns = computedColumns;
        TouchUpdatedAt();
    }
}
