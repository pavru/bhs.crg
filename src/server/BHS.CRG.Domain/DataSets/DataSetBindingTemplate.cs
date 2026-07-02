using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.DataSets;

/// <summary>
/// Шаблон стандартного маппинга набора данных для типа документа.
/// Не содержит ссылку на конкретный файл — только ожидаемые имена колонок.
/// При создании экземпляра маппинг копируется в DataSetBinding.
/// Filter/Transformation/Sort сюда не входят — они на уровне DataSetSource
/// (см. DataSetProcessingTemplate).
/// </summary>
public class DataSetBindingTemplate : Entity
{
    public Guid DocumentTypeId { get; private set; }

    public string Name { get; private set; } = null!;

    /// <summary>null = скалярный; строка = ключ array-поля (табличный).</summary>
    public string? TargetFieldKey { get; private set; }

    /// <summary>JSON: { "ключПоля": "ОжидаемаяКолонкаВФайле" }</summary>
    public string ColumnMappings { get; private set; } = "{}";

    public int SortOrder { get; private set; }

    private DataSetBindingTemplate() { }

    public static DataSetBindingTemplate Create(
        Guid documentTypeId, string name, string? targetFieldKey, string columnMappings, int sortOrder = 0)
        => new()
        {
            DocumentTypeId = documentTypeId,
            Name = name.Trim(),
            TargetFieldKey = targetFieldKey,
            ColumnMappings = columnMappings,
            SortOrder = sortOrder,
        };

    public void Update(string name, string? targetFieldKey, string columnMappings, int sortOrder)
    {
        Name = name.Trim();
        TargetFieldKey = targetFieldKey;
        ColumnMappings = columnMappings;
        SortOrder = sortOrder;
        TouchUpdatedAt();
    }
}
