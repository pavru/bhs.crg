using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

/// <summary>
/// Связь «материал → документ качества» по ИДЕНТИЧНОСТИ материала (артикул/наименование),
/// а не по индексу строки — поэтому переживает переимпорт набора данных. Подмешивается
/// в поле «ДокументПодтверждающийКачетво» при генерации.
/// </summary>
public class MaterialQualityLink : Entity
{
    public CatalogScope Scope { get; private set; }
    public Guid? ScopeId { get; private set; }

    /// <summary>Нормализованный ключ идентичности материала (артикул или наименование).</summary>
    public string MaterialKey { get; private set; } = null!;

    public Guid QualityDocumentId { get; private set; }

    private MaterialQualityLink() { }

    public static MaterialQualityLink Create(CatalogScope scope, Guid? scopeId, string materialKey, Guid qualityDocumentId)
        => new()
        {
            Scope = scope,
            ScopeId = scopeId,
            MaterialKey = materialKey,
            QualityDocumentId = qualityDocumentId,
        };

    public void Retarget(Guid qualityDocumentId)
    {
        QualityDocumentId = qualityDocumentId;
        TouchUpdatedAt();
    }
}
