using BHS.CRG.Domain.Recognition;

namespace BHS.CRG.Application.Recognition;

/// <summary>
/// Источник параметров распознавания (issue #406). Заменяет прямое обращение к классам-константам
/// (<c>GostTitleBlockFields</c> и др.) в точках вызова распознавателя: сами константы остаются
/// исходником сидинга встроенных профилей, а код работает уже с профилем.
/// </summary>
public interface IRecognitionProfileProvider
{
    /// <summary>Встроенный профиль по стабильному коду (<c>BuiltInProfileCodes</c>). Бросает, если
    /// профиля нет — это означает несработавший сидинг, а не пользовательскую ситуацию.</summary>
    Task<ResolvedRecognitionProfile> GetBuiltInAsync(string code, CancellationToken ct = default);

    /// <summary>Профиль по идентификатору (привязка к файлу/группе), либо null.</summary>
    Task<ResolvedRecognitionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Встроенный табличный профиль по функциональному тэгу документа, либо null —
    /// тэг не табличный.</summary>
    Task<ResolvedRecognitionProfile?> GetForTagAsync(string tag, CancellationToken ct = default);

    /// <summary>Есть ли для тэга табличный профиль — предикат «это тэг таблицы».</summary>
    bool IsTableTag(string tag);

    /// <summary>Все профили заданного вида (для выбора в UI).</summary>
    Task<IReadOnlyList<RecognitionProfile>> ListByKindAsync(RecognitionProfileKind kind, CancellationToken ct = default);
}
