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

    /// <summary>
    /// Документ с реквизитами, либо null.
    /// </summary>
    /// <param name="resolveRefs">
    /// Развернуть ссылки на каталог, наследование <c>_baseRef</c>, значения по умолчанию, перечисления
    /// и расчётные поля — то есть привести реквизиты к тому виду, в котором они попадают в PDF (#421).
    /// Без этого организации и лица остаются UUID-ами, а унаследованные поля выглядят незаполненными.
    ///
    /// Наборы данных и документы качества сюда НЕ подмешиваются: они вносят неограниченное число строк,
    /// а форма ответа не выражает <c>truncated</c> — вышла бы та самая тихая неполнота, от которой
    /// защищает страничность строк источников. Табличные данные остаются за инструментами наборов.
    ///
    /// <c>false</c> отдаёт форму хранения: она точнее для сравнения тождества — сопоставить
    /// <c>entryId</c> надёжнее, чем строки имён.
    /// </param>
    Task<DocumentDetail?> GetDocumentAsync(Guid documentId, bool resolveRefs = true,
        CancellationToken ct = default);

    /// <summary>Схема типа документа — ключ к интерпретации реквизитов.</summary>
    Task<DocumentTypeSchemaInfo?> GetDocumentTypeAsync(Guid typeId, CancellationToken ct = default);

    /// <summary>Записи каталога (общие данные): организации, лица, объекты.</summary>
    Task<IReadOnlyList<CatalogEntrySummary>> ListCatalogEntriesAsync(
        string? scope, Guid? scopeId, Guid? typeId, string? search, CancellationToken ct = default);

    /// <summary>Запись каталога как хранится, либо null.</summary>
    Task<CatalogEntryDetail?> GetCatalogEntryAsync(Guid entryId, CancellationToken ct = default);

    /// <summary>
    /// Действующая карта «материал → документ качества» для комплекта (#423): та же цепочка
    /// Set → Section → Construction → System, что и при генерации, где более узкий уровень побеждает.
    ///
    /// Действующая, а не сырая по одному уровню: агента интересует, что реально применится, а
    /// перебирать четыре уровня и воспроизводить правило приоритета — значит просить его повторить
    /// логику системы и рано или поздно разойтись с ней.
    /// </summary>
    Task<IReadOnlyList<MaterialQualityLinkInfo>> ListMaterialQualityLinksAsync(
        Guid setId, CancellationToken ct = default);

    /// <summary>Документы качества (сертификаты/декларации) по области.</summary>
    Task<IReadOnlyList<QualityDocumentSummary>> ListQualityDocumentsAsync(
        string? scope, Guid? scopeId, string? search, CancellationToken ct = default);
}
