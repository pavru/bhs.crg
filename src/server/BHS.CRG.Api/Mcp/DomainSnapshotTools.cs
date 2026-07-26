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
        """)]
    public async Task<IReadOnlyList<ConstructionSummary>> ListConstructionsAsync(CancellationToken ct)
        => await domain.ListConstructionsAsync(CurrentUserId, ct);

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
        Реквизиты документа сырым JSON. Ключи объясняет схема его типа — вызовите get_document_type
        с полем typeId из ответа, иначе значения не интерпретируемы.
        """)]
    public async Task<DocumentDetail?> GetDocumentAsync(
        [Description("Идентификатор документа.")] Guid documentId, CancellationToken ct)
        => await domain.GetDocumentAsync(documentId, ct);

    [McpServerTool(Name = "get_document_type", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Схема типа документа")]
    [Description("""
        Схема типа: какие поля есть, их ключи, типы и заголовки. Нужна, чтобы понять реквизиты
        документа — они хранятся как свободный JSON по этой схеме.
        """)]
    public async Task<DocumentTypeSchemaInfo?> GetDocumentTypeAsync(
        [Description("Идентификатор типа документа.")] Guid typeId, CancellationToken ct)
        => await domain.GetDocumentTypeAsync(typeId, ct);

    [McpServerTool(Name = "list_quality_documents", ReadOnly = true, Idempotent = true, Destructive = false,
        Title = "Документы качества")]
    [Description("""
        Документы качества (сертификаты, декларации соответствия, паспорта) с их реквизитами.
        Это то, что заведено В СИСТЕМЕ, — в отличие от ссылок на сертификаты внутри реестров-файлов;
        сопоставление одного с другим и есть типовая проверка непротиворечивости.
        """)]
    public async Task<IReadOnlyList<QualityDocumentSummary>> ListQualityDocumentsAsync(
        CancellationToken ct,
        [Description("Фильтр области: System, Construction, Section, Set.")] string? scope = null,
        [Description("Идентификатор области (если указан scope).")] Guid? scopeId = null,
        [Description("Поиск по наименованию.")] string? search = null)
        => await domain.ListQualityDocumentsAsync(scope, scopeId, search, ct);
}
