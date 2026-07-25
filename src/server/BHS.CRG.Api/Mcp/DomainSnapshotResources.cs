using System.ComponentModel;
using BHS.CRG.Application.DataSnapshots;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BHS.CRG.Api.Mcp;

/// <summary>
/// URI-адресуемое чтение домена (issue #419) — когда пользователь хочет прикрепить к диалогу
/// конкретную стройку, комплект или документ как контекст, а не полагаться на вызов инструмента.
/// Делегирует в тот же <see cref="IDomainSnapshotService"/>: вторая форма адресации, не вторая логика.
/// </summary>
[McpServerResourceType]
public class DomainSnapshotResources(IDomainSnapshotService domain)
{
    [McpServerResource(UriTemplate = "bhs://construction/{constructionId}", Name = "construction",
        Title = "Стройка", MimeType = "application/json")]
    [Description("Разделы стройки и комплекты документов в них.")]
    public async Task<ResourceContents> GetConstructionAsync(Guid constructionId, CancellationToken ct)
        => McpJsonResource.Json($"bhs://construction/{constructionId}", await domain.GetConstructionAsync(constructionId, ct));

    [McpServerResource(UriTemplate = "bhs://document-set/{setId}", Name = "document-set",
        Title = "Комплект документов", MimeType = "application/json")]
    [Description("Состав комплекта: документы с типами и статусами, контекст раздела и стройки.")]
    public async Task<ResourceContents> GetDocumentSetAsync(Guid setId, CancellationToken ct)
        => McpJsonResource.Json($"bhs://document-set/{setId}", await domain.GetDocumentSetAsync(setId, ct));

    [McpServerResource(UriTemplate = "bhs://document/{documentId}", Name = "document",
        Title = "Документ", MimeType = "application/json")]
    [Description("Реквизиты документа; ключи объясняет схема его типа.")]
    public async Task<ResourceContents> GetDocumentAsync(Guid documentId, CancellationToken ct)
        => McpJsonResource.Json($"bhs://document/{documentId}", await domain.GetDocumentAsync(documentId, ct));

    [McpServerResource(UriTemplate = "bhs://document-type/{typeId}", Name = "document-type",
        Title = "Схема типа документа", MimeType = "application/json")]
    [Description("Схема типа: ключи, типы и заголовки полей — без неё реквизиты не интерпретируемы.")]
    public async Task<ResourceContents> GetDocumentTypeAsync(Guid typeId, CancellationToken ct)
        => McpJsonResource.Json($"bhs://document-type/{typeId}", await domain.GetDocumentTypeAsync(typeId, ct));
}
