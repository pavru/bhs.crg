using BHS.CRG.Domain.Templates;
using MediatR;

namespace BHS.CRG.Application.Templates;

public record CreateTemplateCommand(Guid DocumentTypeId, string Name, string Content) : IRequest<Template>;

/// <summary>Результат мутации шаблона, влияющей на вывод (issue #362): шаблон + число документов,
/// сброшенных в Draft из-за устаревшего PDF (для тоста-предупреждения на фронте).</summary>
public record TemplateMutationResult(Template Template, int ResetDocuments);

/// <summary>Создаёт новую версию шаблона (форк содержимого), опц. с примечанием к версии (issue #360).</summary>
public record UpdateTemplateCommand(Guid Id, string Content, string? Comment = null) : IRequest<TemplateMutationResult>;

/// <summary>Правит содержимое текущей активной версии на месте, без создания новой (issue #360, Ctrl+S).</summary>
public record SaveTemplateContentCommand(Guid Id, string Content) : IRequest<TemplateMutationResult>;
public record DuplicateTemplateCommand(Guid Id, string? NewName) : IRequest<Template>;

/// <summary>Удаление версии шаблона (issue #364). Если на версию запиннуты документы:
/// <paramref name="ReassignUsersToDefault"/>=false → отказ (защита bulk/случайного удаления);
/// =true → снять пин у документов (→ резолв в дефолт) + сброс в Draft, затем удалить.</summary>
public record DeleteTemplateCommand(Guid Id, bool ReassignUsersToDefault = false) : IRequest;

/// <summary>Использование версий шаблона (issue #364): по типу документа — сколько документов
/// запиннуто на каждую версию (для защиты в bulk-очистке и предупреждения при индивидуальном удалении).
/// Ключ — templateId; версии без пинов в словарь не попадают (на фронте = 0).</summary>
public record GetTemplatesUsageQuery(Guid DocumentTypeId) : IRequest<IReadOnlyDictionary<Guid, TemplateUsage>>;

/// <summary>Сколько документов запиннуто на версию + примеры имён (для диалога удаления).</summary>
public record TemplateUsage(int Count, IReadOnlyList<string> Names);

public record GetActiveTemplateQuery(Guid DocumentTypeId) : IRequest<Template?>;
public record ListTemplatesQuery(Guid DocumentTypeId) : IRequest<IReadOnlyList<Template>>;

public record SetTemplateDefaultCommand(Guid Id) : IRequest<TemplateMutationResult>;

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
