using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Templates;

public record CreateTemplateCommand(Guid DocumentTypeId, string Name, string Content) : IRequest<Template>;

/// <summary>Создаёт новую версию шаблона (форк содержимого), опц. с примечанием к версии (issue #360).</summary>
public record UpdateTemplateCommand(Guid Id, string Content, string? Comment = null) : IRequest<Template>;

/// <summary>Правит содержимое текущей активной версии на месте, без создания новой (issue #360, Ctrl+S).</summary>
public record SaveTemplateContentCommand(Guid Id, string Content) : IRequest<Template>;
public record DuplicateTemplateCommand(Guid Id, string? NewName) : IRequest<Template>;
public record DeleteTemplateCommand(Guid Id) : IRequest;
public record GetActiveTemplateQuery(Guid DocumentTypeId) : IRequest<Template?>;
public record ListTemplatesQuery(Guid DocumentTypeId) : IRequest<IReadOnlyList<Template>>;

public record SetTemplateDefaultCommand(Guid Id) : IRequest<Template>;

/// <summary>Объявление параметров шаблона (JSON-массив [{name,label,type,default}] или null).</summary>
public record UpdateTemplateParametersCommand(Guid Id, string? Parameters) : IRequest<Template>;

// ── TemplateAsset (issue #62) ────────────────────────────────────────────────────

public record ListTemplateAssetsQuery(TemplateAssetScope Scope, Guid? ScopeId) : IRequest<IReadOnlyList<TemplateAsset>>;

public record CreateTemplateAssetCommand(
    TemplateAssetScope Scope, Guid? ScopeId, TemplateAssetKind Kind,
    string Name, string FileName, string MimeType, string BlobPath, string? FontFamilyName
) : IRequest<TemplateAsset>;

public record ReplaceTemplateAssetCommand(
    Guid Id, string FileName, string MimeType, string BlobPath, string? FontFamilyName
) : IRequest<TemplateAsset>;

public record DeleteTemplateAssetCommand(Guid Id) : IRequest;
