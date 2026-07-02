using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

/// <summary>
/// Привязка набора данных к документу — только Mapping (колонка → поле).
/// Filter/Transformation/Sort — на уровне DataSetSource, см. DataSetSource.SetProcessing.
/// </summary>
public class DataSetBinding : Entity
{
    /// <summary>Владелец — ровно одно из InstanceId/CommonDataEntryId задано.</summary>
    public Guid? InstanceId { get; private set; }
    public Guid? CommonDataEntryId { get; private set; }
    public Guid SourceId { get; private set; }

    /// <summary>
    /// null — скалярный режим: первая строка → реквизиты по маппингу.
    /// Задано — табличный режим: все строки → array-поле с этим ключом.
    /// </summary>
    public string? TargetFieldKey { get; private set; }

    /// <summary>JSON: { "ключПоляДокумента": "НазваниеКолонки" }</summary>
    public string Mapping { get; private set; } = "{}";

    public DataSetSource Source { get; private set; } = null!;

    private DataSetBinding() { }

    public static DataSetBinding ForInstance(Guid instanceId, Guid sourceId, string? targetFieldKey, string mapping)
        => new()
        {
            InstanceId = instanceId,
            SourceId = sourceId,
            TargetFieldKey = targetFieldKey,
            Mapping = mapping,
        };

    public static DataSetBinding ForCommonDataEntry(Guid commonDataEntryId, Guid sourceId, string? targetFieldKey, string mapping)
        => new()
        {
            CommonDataEntryId = commonDataEntryId,
            SourceId = sourceId,
            TargetFieldKey = targetFieldKey,
            Mapping = mapping,
        };

    public void Update(string? targetFieldKey, string mapping)
    {
        TargetFieldKey = targetFieldKey;
        Mapping = mapping;
        TouchUpdatedAt();
    }
}
