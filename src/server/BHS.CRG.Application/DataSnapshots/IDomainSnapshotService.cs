namespace BHS.CRG.Application.DataSnapshots;

/// <summary>
/// Чтение домена для внешнего потребителя (issue #419). Собирает готовые к отдаче формы поверх
/// существующих запросов и репозиториев — чтобы MCP-слой остался тонким адаптером, как и эндпоинты.
///
/// Списки здесь навигационные и естественно ограничены (стройки, комплекты, документы комплекта),
/// поэтому отдаются целиком — в отличие от строк источников (#415), где страничность обязательна:
/// там молчаливое усечение делает сверку неверной.
/// </summary>
public interface IDomainSnapshotService
{
    /// <summary>Стройки — точка входа в домен.</summary>
    Task<IReadOnlyList<ConstructionSummary>> ListConstructionsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Разделы и комплекты стройки, либо null.</summary>
    Task<ConstructionDetail?> GetConstructionAsync(Guid constructionId, CancellationToken ct = default);

    /// <summary>Комплект с его документами, либо null.</summary>
    Task<DocumentSetDetail?> GetDocumentSetAsync(Guid setId, CancellationToken ct = default);

    /// <summary>Документ с реквизитами, либо null.</summary>
    Task<DocumentDetail?> GetDocumentAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>Схема типа документа — ключ к интерпретации реквизитов.</summary>
    Task<DocumentTypeSchemaInfo?> GetDocumentTypeAsync(Guid typeId, CancellationToken ct = default);

    /// <summary>Документы качества (сертификаты/декларации) по области.</summary>
    Task<IReadOnlyList<QualityDocumentSummary>> ListQualityDocumentsAsync(
        string? scope, Guid? scopeId, string? search, CancellationToken ct = default);
}
