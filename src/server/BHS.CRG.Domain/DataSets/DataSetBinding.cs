using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

/// <summary>
/// Привязка набора данных к объекту — только Mapping (колонка → поле).
/// Filter/Transformation/Sort — на уровне DataSetSource, см. DataSetSource.SetProcessing.
/// Владелец — единый <see cref="OwnerId"/> (issue #84: было InstanceId/CommonDataEntryId).
/// Природа владельца (документ vs общие данные) определяется самим объектом, а не биндингом.
/// </summary>
public class DataSetBinding : Entity
{
    /// <summary>Объект-владелец (DomainObject) — документ комплекта либо запись общих данных.</summary>
    public Guid OwnerId { get; private set; }
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

    public static DataSetBinding For(Guid ownerId, Guid sourceId, string? targetFieldKey, string mapping)
        => new()
        {
            OwnerId = ownerId,
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
