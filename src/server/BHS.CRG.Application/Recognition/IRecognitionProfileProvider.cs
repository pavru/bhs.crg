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

    /// <summary>
    /// Таблична ли группа листов: привязан ТАБЛИЧНЫЙ профиль ИЛИ есть табличный тэг (issue #410).
    /// Единственная реализация предиката на всю систему — он используется и для показа кандидата
    /// источника, и для решения «источник осиротел» (там по нему УДАЛЯЮТСЯ данные), поэтому две
    /// разошедшиеся копии молча сносили бы источники произвольных таблиц.
    /// </summary>
    Task<bool> IsTableGroupAsync(Guid? profileId, IReadOnlyList<string>? tags, CancellationToken ct = default);

    /// <summary>Все профили заданного вида (для выбора в UI).</summary>
    Task<IReadOnlyList<RecognitionProfile>> ListByKindAsync(RecognitionProfileKind kind, CancellationToken ct = default);

    /// <summary>Описание вида для UI и валидации — единственный источник знания о том, что вид
    /// означает (какие части профиля применимы, какие поля защищены). Бросает для неизвестного вида.</summary>
    RecognitionKindInfo DescribeKind(RecognitionProfileKind kind);

    /// <summary>Все известные виды — для выбора при создании профиля.</summary>
    IReadOnlyList<RecognitionKindInfo> ListKinds();

    /// <summary>Переутвердить встроенные профили из кода (после «сбросить к заводским», чтобы
    /// заводское содержимое вернулось сразу, а не на следующем старте).</summary>
    Task ReseedBuiltInAsync(CancellationToken ct = default);
}
