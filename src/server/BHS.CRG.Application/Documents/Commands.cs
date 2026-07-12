using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Documents;

// --- DocumentType ---
public record CreateDocumentTypeCommand(string Name, string Code, DocumentTypeKind Kind, Guid? ParentId, JsonDocument Schema, bool IsAbstract = false) : IRequest<DocumentType>;
public record UpdateDocumentTypeCommand(Guid Id, string Name, string Code, Guid? ParentId) : IRequest<DocumentType>;
public record UpdateDocumentTypeSchemaCommand(Guid Id, JsonDocument Schema) : IRequest<DocumentType>;
public record SetDocumentTypeAbstractCommand(Guid Id, bool IsAbstract) : IRequest<DocumentType>;
public record SetDocumentTypeGroupCommand(Guid Id, string? Group) : IRequest<DocumentType>;
public record DeleteDocumentTypeCommand(Guid Id) : IRequest;
public record ListDocumentTypesQuery(DocumentTypeKind? Kind = null) : IRequest<IReadOnlyList<DocumentType>>;
public record GetDocumentTypeQuery(Guid Id) : IRequest<DocumentType?>;

// --- Construction ---
public record CreateConstructionCommand(string Name, Guid UserId) : IRequest<Construction>;
public record RenameConstructionCommand(Guid Id, string Name) : IRequest<Construction>;
public record DeleteConstructionCommand(Guid Id) : IRequest;
public record GetConstructionQuery(Guid Id) : IRequest<Construction?>;
public record ListConstructionsQuery(Guid UserId) : IRequest<IReadOnlyList<Construction>>;

// --- Section ---
public record CreateSectionCommand(Guid ConstructionId, string Name) : IRequest<Section>;
public record RenameSectionCommand(Guid Id, string Name) : IRequest<Section>;
public record DeleteSectionCommand(Guid Id) : IRequest;

// --- DocumentSet ---
public record CreateDocumentSetCommand(Guid SectionId, string Name) : IRequest<DocumentSet>;
public record RenameDocumentSetCommand(Guid Id, string Name) : IRequest<DocumentSet>;
public record DeleteDocumentSetCommand(Guid Id) : IRequest;
public record GetDocumentSetQuery(Guid Id) : IRequest<DocumentSet?>;
public record ListAvailableInstancesQuery(Guid SetId) : IRequest<IReadOnlyList<DocumentInstance>>;

// --- DocumentInstance ---
public record AddDocumentToSetCommand(Guid DocumentSetId, Guid DocumentTypeId) : IRequest<DocumentInstance>;
public record ReorderDocumentInstancesCommand(Guid SetId, IReadOnlyList<Guid> OrderedInstanceIds) : IRequest<DocumentSet>;
public record RenameDocumentInstanceCommand(Guid Id, string? Name) : IRequest<DocumentInstance>;
public record DeleteDocumentInstanceCommand(Guid Id) : IRequest;
public record UpdateRequisitesCommand(Guid InstanceId, JsonDocument Requisites) : IRequest<DocumentInstance>;
public record UpdatePluginDataCommand(Guid InstanceId, JsonDocument PluginData) : IRequest<DocumentInstance>;
public record GetDocumentInstanceQuery(Guid Id) : IRequest<DocumentInstance?>;
public record SetDocumentTemplateCommand(Guid InstanceId, Guid? TemplateId) : IRequest<DocumentInstance>;

/// <summary>Набор выбранных шаблонов для мульти-генерации (JSON-массив Guid или null — тогда один дефолт).</summary>
public record SetDocumentTemplatesCommand(Guid InstanceId, string? TemplateIds) : IRequest<DocumentInstance>;

/// <summary>Переопределения значений параметров шаблона на документе (JSON-объект {имя:значение} или null).</summary>
public record SetDocumentTemplateParamsCommand(Guid InstanceId, string? Params) : IRequest<DocumentInstance>;

// --- CommonDataEntry ---
public record CreateCommonDataEntryCommand(
    string DisplayName, Guid CompositeTypeId, JsonDocument Data,
    CatalogScope Scope, Guid? ScopeId, IReadOnlyList<string>? Aliases = null) : IRequest<CommonDataEntry>;

public record UpdateCommonDataEntryCommand(Guid Id, string DisplayName, JsonDocument Data,
    IReadOnlyList<string>? Aliases = null) : IRequest<CommonDataEntry>;

public record DeleteCommonDataEntryCommand(Guid Id) : IRequest;

public record ListCommonDataEntriesQuery(
    CatalogScope? Scope = null,
    Guid? ScopeId = null,
    Guid? CompositeTypeId = null) : IRequest<IReadOnlyList<CommonDataEntry>>;

/// <summary>
/// Возвращает все записи каталога, доступные для данного комплекта,
/// с учётом иерархии скоупов: Set → Section → Construction → System.
/// </summary>
public record ResolveCommonDataForSetQuery(Guid SetId, Guid? CompositeTypeId = null)
    : IRequest<IReadOnlyList<CommonDataEntryWithScope>>;

/// <summary>
/// Записи общих данных, видимые из ЛЮБОГО скопа (issue #82): резолвит полную родительскую цепочку
/// (Set→Section→Construction→System) — в отличие от <see cref="ResolveCommonDataForSetQuery"/>,
/// который стартует только с комплекта. Нужен, чтобы из раздел/строечного объекта ссылаться на
/// объекты более широких уровней.
/// </summary>
public record ResolveCommonDataForScopeQuery(CatalogScope Scope, Guid? ScopeId, Guid? CompositeTypeId = null)
    : IRequest<IReadOnlyList<CommonDataEntryWithScope>>;

public record CommonDataEntryWithScope(
    Guid Id,
    string DisplayName,
    Guid CompositeTypeId,
    JsonDocument Data,
    CatalogScope Scope,
    Guid? ScopeId,
    int Priority,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
