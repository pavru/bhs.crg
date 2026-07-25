using MediatR;

namespace BHS.CRG.Application.Recognition;

// CRUD профилей распознавания (issue #408) — по конвенции проекта через MediatR, как у реестров
// типов полей/перечислений. Вид (Kind) задаётся только при создании: он выбирает применяемый промпт,
// смена вида сделала бы профиль другой сущностью.

public record ListRecognitionProfilesQuery : IRequest<IReadOnlyList<RecognitionProfileDto>>;

public record ListRecognitionKindsQuery : IRequest<IReadOnlyList<RecognitionKindInfo>>;

public record CreateRecognitionProfileCommand(
    string Name,
    string Kind,
    IReadOnlyList<RecognitionProfileField> Fields,
    IReadOnlyList<RecognitionProfileField> RowColumns,
    RecognitionTableShape? Shape
) : IRequest<RecognitionProfileDto>;

public record UpdateRecognitionProfileCommand(
    Guid Id,
    string Name,
    IReadOnlyList<RecognitionProfileField> Fields,
    IReadOnlyList<RecognitionProfileField> RowColumns,
    RecognitionTableShape? Shape
) : IRequest<RecognitionProfileDto>;

public record DeleteRecognitionProfileCommand(Guid Id) : IRequest;

/// <summary>«Сбросить к заводским» — снимает отметку о правке; заводское содержимое возвращает
/// сидер (сразу же, в том же запросе).</summary>
public record ResetRecognitionProfileCommand(Guid Id) : IRequest<RecognitionProfileDto>;
