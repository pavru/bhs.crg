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

/// <summary>Пакетно привязывает набор материалов (по ключам идентичности) к одному документу.</summary>
public record SetMaterialLinksCommand(
    CatalogScope Scope, Guid? ScopeId, IReadOnlyList<string> MaterialKeys, Guid QualityDocumentId) : IRequest<int>;

public record RemoveMaterialLinkCommand(Guid Id) : IRequest;

public record ListMaterialLinksQuery(CatalogScope Scope, Guid? ScopeId) : IRequest<IReadOnlyList<MaterialQualityLink>>;
