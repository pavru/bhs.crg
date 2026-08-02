using System.ComponentModel;
using System.Security.Claims;
using BHS.CRG.Application.DataSnapshots;
using ModelContextProtocol.Server;

namespace BHS.CRG.Api.Mcp;

/// <summary>
/// Чтение ДОМЕНА для внешнего агента (issue #419) — в дополнение к чтению наборов данных (#415).
/// Наборы отвечают «что в файлах», эти инструменты — «что об этом знает сама система».
///
/// Слой тонкий: разбор аргументов и вызов <see cref="IDomainSnapshotService"/>, сборка — там.
/// Инструментов записи здесь по-прежнему НЕТ.
/// </summary>
[McpServerToolType]
public class DomainSnapshotTools(IDomainSnapshotService domain, IHttpContextAccessor http)
{
    /// <summary>Агент действует ОТ ИМЕНИ пользователя — идентичность берём из его же JWT.</summary>
    private Guid CurrentUserId
    {
        get
        {
            var user = http.HttpContext?.User;
            var raw = user?.FindFirstValue(ClaimTypes.NameIdentifier) ?? user?.FindFirstValue("sub");
            return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
        }
    }

    [McpServerTool(Name = "list_constructions", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Стройки")]
    [Description("""
        Стройки (объекты строительства) — точка входа в домен. Возвращает идентификаторы и сводку:
        сколько разделов, комплектов и документов. Дальше — get_construction.

        Ответ — страница {items, offset, limit, total, truncated}, как у всех списков.
        """)]
    public async Task<SnapshotPage<ConstructionSummary>> ListConstructionsAsync(
        CancellationToken ct,
        [Description("Смещение от начала (0 — с первой стройки).")] int offset = 0,
        [Description("Сколько строек вернуть; по умолчанию 200, максимум 500.")]
        int limit = DomainSnapshotLimits.NavigationDefault)
        => await domain.ListConstructionsAsync(CurrentUserId, offset, limit, ct);

    [McpServerTool(Name = "get_construction", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Разделы и комплекты стройки")]
    [Description("Разделы стройки (ЭОМ, СС, ВК, ОВиК и т.п.) и комплекты документов в каждом, с числом документов.")]
    public async Task<ConstructionDetail?> GetConstructionAsync(
        [Description("Идентификатор стройки.")] Guid constructionId, CancellationToken ct)
        => await domain.GetConstructionAsync(constructionId, ct);

    [McpServerTool(Name = "get_document_set", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Документы комплекта")]
    [Description("""
        Состав комплекта: документы с типом и статусом, плюс контекст (раздел и стройка) — имена
        документов между разделами повторяются, и без контекста находку не проверить.
        """)]
    public async Task<DocumentSetDetail?> GetDocumentSetAsync(
        [Description("Идентификатор комплекта документов.")] Guid setId, CancellationToken ct)
        => await domain.GetDocumentSetAsync(setId, ct);

    [McpServerTool(Name = "get_document", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Документ с реквизитами")]
    [Description("""
        Реквизиты документа. Ключи объясняет схема его типа — вызовите get_document_type с полем
        typeId из ответа, иначе значения не интерпретируемы.

        По умолчанию ссылки развёрнуты: организации и лица приходят данными, а не идентификаторами,
        унаследованные поля подмешаны, коды перечислений заменены именами — то есть реквизиты в том
        виде, в котором попадают в PDF. Поле refsResolved говорит, какую форму вы держите.

        Каждая запись каталога развёрнута ОДИН раз — в словаре entities, а по месту стоит
        {"$entity":"<id>"}. Одна организация, упомянутая трижды, и есть одна организация: тождество
        проверяется по идентификатору, а не сравнением карточек.

        Реквизиты ДРУГИХ документов не разворачиваются: на их месте {"$document":"<id>",
        "displayName":"…"}. Нужен сам документ — возьмите его отдельным вызовом или попросите
        expandDocumentRefs=true.

        Табличных данных здесь нет: наборы данных и документы качества сюда не подмешиваются, потому
        что число строк не ограничено. За таблицами — get_rows, там есть признак усечения.

        Зато есть tableFields — перечень табличных полей типа с адресом их строк: boundToDataset,
        sourceId (и datasetId), rowCount после фильтра источника. boundToDataset=false означает
        именно пустую таблицу; отсутствие ключа таблицы в реквизитах ничего не означает — их там не
        бывает вовсе. За строками идите в get_rows по sourceId.

        Нужны два-три поля — перечислите их в fields: документ придёт только с ними. Ответ скажет,
        что был урезан (projectedFields), и назовёт ключи, которых в схеме нет (unknownFields), —
        опечатка в ключе иначе выглядит как незаполненное поле.
        """)]
    public async Task<DocumentDetail?> GetDocumentAsync(
        [Description("Идентификатор документа.")] Guid documentId,
        CancellationToken ct,
        [Description("""
            Развернуть ссылки, наследование и перечисления (по умолчанию да). Укажите false, чтобы
            получить форму хранения: для сравнения тождества сопоставить entryId надёжнее, чем имена.
            """)] bool resolveRefs = true,
        [Description("""
            Ключи полей верхнего уровня, которыми ограничить реквизиты (например
            ["НомерДокумента","Подписи"]). Пусто — весь документ. Ключи берите из схемы типа
            (get_document_type); значения при этом те же, что и без ограничения, — разбор идёт
            полный, урезается только ответ.
            """)] string[]? fields = null,
        [Description("""
            Развернуть реквизиты документов, на которые ссылается этот (по умолчанию нет — приходит
            ссылка с наименованием). Полная копия чужого акта со всеми его организациями весит
            больше, чем весь остальной ответ, поэтому просите её, только если сравниваете значения
            внутри неё.
            """)] bool expandDocumentRefs = false)
        => await domain.GetDocumentAsync(documentId, resolveRefs, fields, expandDocumentRefs, ct);

    [McpServerTool(Name = "list_catalog_entries", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Каталог: записи")]
    [Description("""
        Записи каталога (общие данные): организации, лица, объекты строительства. Отвечает на вопросы
        про сам каталог — заведена ли такая организация, какие лица есть на стройке, — а не про
        конкретный документ.

        Выборка страничная: продолжайте запрашивать со смещением, пока truncated=true, и сверяйте
        total с числом полученных записей. Порядок — по наименованию — устойчив между вызовами.
        """)]
    public async Task<SnapshotPage<CatalogEntrySummary>> ListCatalogEntriesAsync(
        CancellationToken ct,
        [Description("Фильтр уровня: System, Construction, Section, Set.")] string? scope = null,
        [Description("Идентификатор уровня (если указан scope).")] Guid? scopeId = null,
        [Description("Фильтр по типу записи.")] Guid? typeId = null,
        [Description("Поиск по наименованию.")] string? search = null,
        [Description("Смещение от начала (0 — с первой записи).")] int offset = 0,
        [Description("Сколько записей вернуть; по умолчанию 100, максимум 500.")]
        int limit = DomainSnapshotLimits.CatalogEntriesDefault)
        => await domain.ListCatalogEntriesAsync(scope, scopeId, typeId, search, offset, limit, ct);

    [McpServerTool(Name = "get_catalog_entry", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Каталог: запись")]
    [Description("""
        Запись каталога с её данными. Сюда же ведёт ссылка из реквизитов, полученных с
        resolveRefs=false: поле entryId такой ссылки — идентификатор для этого инструмента.
        Вложенные ссылки внутри записи проходятся тем же способом.
        """)]
    public async Task<CatalogEntryDetail?> GetCatalogEntryAsync(
        [Description("Идентификатор записи каталога.")] Guid entryId, CancellationToken ct)
        => await domain.GetCatalogEntryAsync(entryId, ct);

    [McpServerTool(Name = "get_document_type", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Схема типа документа")]
    [Description("""
        Схема типа: какие поля есть, их ключи, типы и заголовки. Нужна, чтобы понять реквизиты
        документа — они хранятся как свободный JSON по этой схеме.
        """)]
    public async Task<DocumentTypeSchemaInfo?> GetDocumentTypeAsync(
        [Description("Идентификатор типа документа.")] Guid typeId, CancellationToken ct)
        => await domain.GetDocumentTypeAsync(typeId, ct);

    [McpServerTool(Name = "list_material_quality_links", ReadOnly = true, Idempotent = true,
        Destructive = false, Title = "Связи материал → документ качества")]
    [Description("""
        Действующая карта «материал → документ качества» для комплекта: какой сертификат система
        подставит какому материалу. Ровно то, что попадёт в документ при генерации.

        Ключ материала — нормализованный артикул или наименование; он же связывает карту со строками
        наборов данных, поэтому расхождение «в реестре указан один сертификат, а привязан другой»
        находится сопоставлением этой карты с get_rows.

        Уровень в ответе — откуда связь пришла (Set / Section / Construction / System). Связь может
        быть заведена на System и неожиданно действовать на конкретном комплекте.

        Выборка страничная: карта на крупной стройке насчитывает сотни позиций. Листайте по
        смещению, пока truncated=true, — по неполной карте вывод «сертификат не привязан» неверен.
        """)]
    public async Task<SnapshotPage<MaterialQualityLinkInfo>> ListMaterialQualityLinksAsync(
        [Description("Идентификатор комплекта документов.")] Guid setId,
        CancellationToken ct,
        [Description("""
            Только связи, изменившиеся после этого момента (ISO 8601). Для повторной проверки:
            карта на сотни позиций, а между прогонами меняются единицы. ВНИМАНИЕ: удаления так не
            видны — пропавшая связь не «изменилась», её замечает только полное чтение.
            """)] DateTimeOffset? changedSince = null,
        [Description("Смещение от начала (0 — с первой связи).")] int offset = 0,
        [Description("Сколько связей вернуть; по умолчанию 200, максимум 500.")]
        int limit = DomainSnapshotLimits.MaterialLinksDefault)
        => await domain.ListMaterialQualityLinksAsync(setId, changedSince, offset, limit, ct);

    [McpServerTool(Name = "list_quality_documents", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Документы качества")]
    [Description("""
        Документы качества (сертификаты, декларации соответствия, паспорта) с их реквизитами.
        Это то, что заведено В СИСТЕМЕ, — в отличие от ссылок на сертификаты внутри реестров-файлов;
        сопоставление одного с другим и есть типовая проверка непротиворечивости.

        Выборка страничная и мелкая: каждый документ несёт свои реквизиты, включая перечень
        продукции, и это самая тяжёлая запись домена. Листайте по смещению, пока truncated=true;
        сузить выборку заранее дешевле — фильтром области или поиском по наименованию.
        """)]
    public async Task<SnapshotPage<QualityDocumentSummary>> ListQualityDocumentsAsync(
        CancellationToken ct,
        [Description("Фильтр области: System, Construction, Section, Set.")] string? scope = null,
        [Description("Идентификатор области (если указан scope).")] Guid? scopeId = null,
        [Description("Поиск по наименованию.")] string? search = null,
        [Description("""
            Только документы, изменившиеся после этого момента (ISO 8601) — для повторной проверки.
            ВНИМАНИЕ: удаления так не видны, их замечает только полное чтение.
            """)] DateTimeOffset? changedSince = null,
        [Description("Смещение от начала (0 — с первого документа).")] int offset = 0,
        [Description("Сколько документов вернуть; по умолчанию 25, максимум 100.")]
        int limit = DomainSnapshotLimits.QualityDocumentsDefault)
        => await domain.ListQualityDocumentsAsync(scope, scopeId, search, changedSince, offset, limit, ct);
}
