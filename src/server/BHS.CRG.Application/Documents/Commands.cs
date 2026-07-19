using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using MediatR;

namespace BHS.CRG.Application.Documents;

// --- DocumentType ---
public record CreateDocumentTypeCommand(string Name, string Code, DocumentTypeKind Kind, Guid? ParentId, JsonDocument Schema, bool IsAbstract = false) : IRequest<DocumentType>;
public record UpdateDocumentTypeCommand(Guid Id, string Name, string Code, Guid? ParentId) : IRequest<DocumentType>;
public record UpdateDocumentTypeSchemaCommand(Guid Id, JsonDocument Schema) : IRequest<DocumentType>;
public record SetDocumentTypeAbstractCommand(Guid Id, bool IsAbstract) : IRequest<DocumentType>;
public record SetDocumentTypeAllowsProxyCommand(Guid Id, bool AllowsProxy) : IRequest<DocumentType>;
public record SetDocumentTypeGroupCommand(Guid Id, string? Group) : IRequest<DocumentType>;
public record DeleteDocumentTypeCommand(Guid Id) : IRequest;
public record ListDocumentTypesQuery(DocumentTypeKind? Kind = null) : IRequest<IReadOnlyList<DocumentType>>;
public record GetDocumentTypeQuery(Guid Id) : IRequest<DocumentType?>;

/// Использование типа документа (issue #275) — чем занят тип, из-за чего его нельзя удалить.
/// Показывается проактивно (до попытки удаления); те же проверки, что и guard удаления.
public record GetDocumentTypeUsageQuery(Guid Id) : IRequest<DocumentTypeUsage>;

public record DocumentTypeUsage(IReadOnlyList<DocumentTypeUsageReason> Reasons)
{
    public bool InUse => Reasons.Count > 0;
}

/// Одна причина занятости: Kind — машинный вид, Label — человекочитаемо, Count — сколько (0 для
/// булева признака вроде «профиль уровня»), Names — примеры имён (для видов, где они уместны).
public record DocumentTypeUsageReason(string Kind, string Label, int Count, IReadOnlyList<string> Names);

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
public record ListAvailableInstancesQuery(Guid SetId) : IRequest<IReadOnlyList<DomainObject>>;

// --- DocumentInstance ---
public record AddDocumentToSetCommand(Guid DocumentSetId, Guid DocumentTypeId) : IRequest<DomainObject>;
public record ReorderDocumentInstancesCommand(Guid SetId, IReadOnlyList<Guid> OrderedInstanceIds) : IRequest<DocumentSet>;
public record RenameDocumentInstanceCommand(Guid Id, string? Name) : IRequest<DomainObject>;
public record DeleteDocumentInstanceCommand(Guid Id) : IRequest;
/// Дублировать документ в ТОТ ЖЕ комплект (issue #283, фаза B): клон в конец, «Копия …»,
/// все ссылки/_baseRef сохраняются (тот же scope), свежий черновик без PDF.
public record DuplicateDocumentInstanceCommand(Guid InstanceId) : IRequest<DomainObject>;

// --- Копирование в другой комплект (issue #283, фаза C) ---
/// Стратегия обработки исходящих ссылок при копировании/переносе в ДРУГОЙ комплект.
public enum CopyStrategy
{
    /// «Умная очистка» (дефолт): flatten `_baseRef`, стрип `$ref:document/instance`, оставить `$ref:catalog`.
    SmartCleanup,
    // Snapshot (независимый снимок) — фаза C2, пока не реализован.
}

/// Одно предупреждение о воздействии на ссылки (сгруппировано по виду; Names — примеры полей).
public record CopyWarning(string Kind, string Label, int Count, IReadOnlyList<string> Names);

/// Результат копирования: созданный документ + предупреждения о затронутых ссылках.
public record CopyResult(DomainObject Instance, IReadOnlyList<CopyWarning> Warnings);

/// Скопировать документ в другой комплект (оригинал остаётся).
public record CopyDocumentToSetCommand(Guid SourceId, Guid TargetSetId, CopyStrategy Strategy) : IRequest<CopyResult>;

/// Dry-run: какие ссылки затронет копирование/перенос — для превью в диалоге ДО подтверждения.
public record PreviewCopyDocumentQuery(Guid SourceId, Guid TargetSetId, CopyStrategy Strategy) : IRequest<IReadOnlyList<CopyWarning>>;

// --- Перенос в другой комплект (issue #283, фаза D) ---
/// Перенести документ в другой комплект (уходит из текущего). Блокируется, если на него ссылаются
/// (входящий guard, как удаление #269); при переносе — тот же скраб исходящих ссылок, что и copy;
/// сгенерированные PDF сбрасываются (контекст резолва сменился).
public record MoveDocumentToSetCommand(Guid SourceId, Guid TargetSetId, CopyStrategy Strategy) : IRequest<CopyResult>;

/// Превью переноса: затронутые ссылки (как copy) + имена объектов, из-за которых перенос заблокирован.
public record MovePreview(IReadOnlyList<CopyWarning> Warnings, IReadOnlyList<string> BlockedBy);
public record PreviewMoveDocumentQuery(Guid SourceId, Guid TargetSetId, CopyStrategy Strategy) : IRequest<MovePreview>;
public record UpdateRequisitesCommand(Guid InstanceId, JsonDocument Requisites) : IRequest<DomainObject>;
public record UpdatePluginDataCommand(Guid InstanceId, JsonDocument PluginData) : IRequest<DomainObject>;
public record GetDocumentInstanceQuery(Guid Id) : IRequest<DomainObject?>;
public record SetDocumentTemplateCommand(Guid InstanceId, Guid? TemplateId) : IRequest<DomainObject>;

/// <summary>Набор выбранных шаблонов для мульти-генерации (JSON-массив Guid или null — тогда один дефолт).</summary>
public record SetDocumentTemplatesCommand(Guid InstanceId, string? TemplateIds) : IRequest<DomainObject>;

/// <summary>Переопределения значений параметров шаблона на документе (JSON-объект {имя:значение} или null).</summary>
public record SetDocumentTemplateParamsCommand(Guid InstanceId, string? Params) : IRequest<DomainObject>;

// --- CommonDataEntry ---
public record CreateCommonDataEntryCommand(
    string DisplayName, Guid CompositeTypeId, JsonDocument Data,
    CatalogScope Scope, Guid? ScopeId, IReadOnlyList<string>? Aliases = null) : IRequest<DomainObject>;

public record UpdateCommonDataEntryCommand(Guid Id, string DisplayName, JsonDocument Data,
    IReadOnlyList<string>? Aliases = null) : IRequest<DomainObject>;

public record DeleteCommonDataEntryCommand(Guid Id) : IRequest;

public record ListCommonDataEntriesQuery(
    CatalogScope? Scope = null,
    Guid? ScopeId = null,
    Guid? CompositeTypeId = null) : IRequest<IReadOnlyList<DomainObject>>;

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
