namespace BHS.CRG.Application.Templates;

/// <summary>
/// Версия шаблона в списке типа — всё, кроме текста Typst (issue #1972). Список отдаёт все версии
/// всех шаблонов типа, а текст нужен одному редактору и одной версии: он читается отдельным адресом
/// (<see cref="GetTemplateContentQuery"/>). Прежде ответ рос на полный текст с каждым сохранением.
/// </summary>
public sealed record TemplateSummary(
    Guid Id,
    Guid DocumentTypeId,
    string Name,
    int Version,
    string? Parameters,
    string? Comment,
    bool IsActive,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
