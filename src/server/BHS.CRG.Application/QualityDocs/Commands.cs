using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.QualityDocs;

// ─── Библиотека документов качества ─────────────────────────────────────────────

public record CreateQualityDocumentCommand(
    Guid DocumentTypeId, string DisplayName, JsonDocument Requisites,
    CatalogScope Scope, Guid? ScopeId, QualityDocSource Source,
    string? ScanBlobPath, string? ScanFileName, string? ScanMimeType) : IRequest<QualityDocument>;

public record UpdateQualityDocumentCommand(Guid Id, Guid DocumentTypeId, string DisplayName, JsonDocument Requisites) : IRequest<QualityDocument>;

public record SetQualityDocScanCommand(Guid Id, string? ScanBlobPath, string? ScanFileName, string? ScanMimeType) : IRequest<QualityDocument>;

public record DeleteQualityDocumentCommand(Guid Id) : IRequest;

public record GetQualityDocumentQuery(Guid Id) : IRequest<QualityDocument?>;

public record ListQualityDocumentsQuery(CatalogScope? Scope, Guid? ScopeId, string? Search) : IRequest<IReadOnlyList<QualityDocument>>;

// ─── Связи материал → документ качества ─────────────────────────────────────────

/// <summary>
/// Материал для привязки: ключ идентичности и, если есть под рукой, человекочитаемое имя.
/// Имя необязательно — перепривязка с экрана контроля идёт без него (issue #554).
/// </summary>
public record MaterialLinkInput(string Key, string? Label = null);

/// <summary>Пакетно привязывает набор материалов (по ключам идентичности) к одному документу.</summary>
public record SetMaterialLinksCommand(
    CatalogScope Scope, Guid? ScopeId, IReadOnlyList<MaterialLinkInput> Materials, Guid QualityDocumentId) : IRequest<int>;

public record RemoveMaterialLinkCommand(Guid Id) : IRequest;

/// <summary>
/// Связки с именем документа. Область НЕОБЯЗАТЕЛЬНА (issue #554): экран контроля смотрит поперёк
/// областей, а прежняя обязательность заставляла клиента делать запрос на каждую и склеивать руками —
/// причём только System и Set, из-за чего связки уровня раздела и стройки не показывались вовсе.
/// </summary>
public record ListMaterialLinksQuery(CatalogScope? Scope = null, Guid? ScopeId = null)
    : IRequest<IReadOnlyList<MaterialLinkRow>>;

/// <summary>Строка связки для показа: сама связка + имя и тип документа (join, не сущность).</summary>
public record MaterialLinkRow(
    Guid Id, string MaterialKey, string? MaterialLabel, CatalogScope Scope, Guid? ScopeId,
    Guid QualityDocumentId, string QualityDocumentName, string? QualityDocumentType,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
