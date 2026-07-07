using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Templates;

public record CreateTemplateCommand(Guid DocumentTypeId, string Name, string Content) : IRequest<Template>;
public record UpdateTemplateCommand(Guid Id, string Content) : IRequest<Template>;
public record DuplicateTemplateCommand(Guid Id, string? NewName) : IRequest<Template>;
public record DeleteTemplateCommand(Guid Id) : IRequest;
public record GetActiveTemplateQuery(Guid DocumentTypeId) : IRequest<Template?>;
public record ListTemplatesQuery(Guid DocumentTypeId) : IRequest<IReadOnlyList<Template>>;

public record UpdateTemplateSettingsCommand(
    Guid Id,
    string PageSize,
    string PageOrientation,
    int MarginTop,
    int MarginRight,
    int MarginBottom,
    int MarginLeft
) : IRequest<Template>;

public record SetTemplateDefaultCommand(Guid Id) : IRequest<Template>;

/// <summary>Объявление параметров шаблона (JSON-массив [{name,label,type,default}] или null).</summary>
public record UpdateTemplateParametersCommand(Guid Id, string? Parameters) : IRequest<Template>;
