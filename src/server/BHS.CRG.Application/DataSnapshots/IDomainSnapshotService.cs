namespace BHS.CRG.Application.DataSnapshots;

/// <summary>
/// Чтение домена для внешнего потребителя (issue #419). Собирает готовые к отдаче формы поверх
/// существующих запросов и репозиториев — чтобы MCP-слой остался тонким адаптером, как и эндпоинты.
///
/// Списки, растущие вместе с проектом — документы качества, записи каталога, карта материалов, —
/// страничные, как и строки источников (#415): допущение «домен ограничен сам собой» не выдержало
/// встречи с реальными данными (#576), а молчаливое усечение делает сверку неверной.
///
/// Навигационные списки (стройки) с #590 носят ту же оболочку, хотя их длину и задаёт структура
/// стройки: разница в форме между «этот список страничный, а тот голым массивом» стоила клиенту
/// тихого нуля записей — он читал <c>items</c> там, где приходил массив, и наоборот.
///
/// Состав деталей (разделы стройки, документы комплекта) остаётся частью САМОЙ детали и оболочки не
/// получает: это не отдельная выдача, а поле объекта.
/// </summary>
public interface IDomainSnapshotService
{
    /// <summary>Стройки — точка входа в домен, страницей (#590).</summary>
    Task<SnapshotPage<ConstructionSummary>> ListConstructionsAsync(
        Guid userId,
        int offset = 0, int limit = DomainSnapshotLimits.NavigationDefault,
        CancellationToken ct = default);

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
    /// <param name="fields">
    /// Ограничить реквизиты этими ключами верхнего уровня (issue #596). Почти каждый вызов делается
    /// ради двух-трёх полей, а документ приходит целиком и фильтруется у вызывающего.
    ///
    /// Резолв выполняется ПОЛНЫЙ и только потом проецируется: расчётное поле читает соседние, а
    /// унаследованное приходит от базового документа — считать «только запрошенное» значило бы
    /// вернуть другое значение, а не то же самое дешевле.
    /// </param>
    /// <param name="expandDocumentRefs">
    /// Оставить развёрнутыми реквизиты ДРУГИХ документов, на которые ссылается этот (issue #595).
    /// По умолчанию на их месте стоит ссылка <c>{$document, displayName}</c>: поле
    /// «ОсновнойДокумент» несло полную копию акта со всеми его организациями, и реестр работ доходил
    /// до 16 МБ. Копия нужна редко, а стоит дороже всего остального вместе.
    /// </param>
    Task<DocumentDetail?> GetDocumentAsync(Guid documentId, bool resolveRefs = true,
        IReadOnlyCollection<string>? fields = null, bool expandDocumentRefs = false,
        CancellationToken ct = default);

    /// <summary>Схема типа документа — ключ к интерпретации реквизитов.</summary>
    Task<DocumentTypeSchemaInfo?> GetDocumentTypeAsync(Guid typeId, CancellationToken ct = default);

    /// <summary>Записи каталога (общие данные): организации, лица, объекты — страницей.</summary>
    Task<SnapshotPage<CatalogEntrySummary>> ListCatalogEntriesAsync(
        string? scope, Guid? scopeId, Guid? typeId, string? search,
        int offset = 0, int limit = DomainSnapshotLimits.CatalogEntriesDefault,
        CancellationToken ct = default);

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
    /// <param name="changedSince">Отдать только связи, изменившиеся после этого момента (issue #598):
    /// из 113 связок комплекта за сессию менялась одна, а список запрашивался целиком. Отбор идёт
    /// ПОСЛЕ схлопывания по приоритету — по тому, что действует. Удаления так не видны: исчезнувшая
    /// связь не «изменилась», она пропала, и заметить это можно только полным чтением.</param>
    Task<SnapshotPage<MaterialQualityLinkInfo>> ListMaterialQualityLinksAsync(
        Guid setId, DateTimeOffset? changedSince = null,
        int offset = 0, int limit = DomainSnapshotLimits.MaterialLinksDefault,
        CancellationToken ct = default);

    /// <summary>Документы качества (сертификаты/декларации) по области — страницей.</summary>
    /// <inheritdoc cref="ListMaterialQualityLinksAsync" path="/param[@name='changedSince']" />
    Task<SnapshotPage<QualityDocumentSummary>> ListQualityDocumentsAsync(
        string? scope, Guid? scopeId, string? search, DateTimeOffset? changedSince = null,
        int offset = 0, int limit = DomainSnapshotLimits.QualityDocumentsDefault,
        CancellationToken ct = default);
}
