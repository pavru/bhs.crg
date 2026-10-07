using BHS.CRG.Domain.Recognition;

namespace BHS.CRG.Application.Recognition;

/// <summary>
/// Источник параметров распознавания (issue #406). Заменяет прямое обращение к перечням полей в
/// точках вызова распознавателя: код работает с профилем, а не с тем, из чего он собран.
///
/// <para>⚠️ Здесь стоят ворота модулей (issue #1075). Каждый метод, отдающий профиль, чтобы им
/// РАСПОЗНАВАТЬ, отвечает отказом с названием модуля, если владелец профиля выключен. Предикаты
/// «это таблица?» ворот не спрашивают — по ним удаляются данные, а выключение модуля данных не
/// трогает.</para>
/// </summary>
public interface IRecognitionProfileProvider
{
    /// <summary>Заводской профиль вида — им читают, когда свой не привязан. Отказ, если владелец
    /// вида выключен или вид не объявлен никем.</summary>
    Task<ResolvedRecognitionProfile> GetDefaultAsync(RecognitionProfileKind kind, CancellationToken ct = default);

    /// <summary>Ворота вида без чтения профиля: отказ, если владелец вида выключен. Для мест,
    /// которые решают «можно ли вообще читать так на этом экземпляре» до того, как что-то читать, —
    /// выбор профиля PDF у набора, планирование распознавания.</summary>
    void RequireKind(RecognitionProfileKind kind);

    /// <summary>Профиль по идентификатору (привязка к файлу/группе), либо null — такого нет.
    /// Профиль выключенного модуля — отказ, а не null: по null потребитель взял бы заводской и
    /// прочитал бы документ не теми параметрами, которые выбрал человек.</summary>
    Task<ResolvedRecognitionProfile?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Встроенный табличный профиль по функциональному тэгу документа, либо null —
    /// тэг не табличный. Отказ, если владелец профиля выключен.</summary>
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

    /// <summary>Описание вида для UI и валидации — единственный источник знания о том, что вид
    /// означает (какие части профиля применимы, какие поля защищены). Бросает для неизвестного вида.</summary>
    RecognitionKindInfo DescribeKind(RecognitionProfileKind kind);

    /// <summary>Виды, доступные на этом экземпляре, — для выбора при создании профиля. Вид
    /// выключенного модуля не предлагается.</summary>
    IReadOnlyList<RecognitionKindInfo> ListKinds();

    /// <summary>Переутвердить встроенные профили из кода (после «сбросить к заводским», чтобы
    /// заводское содержимое вернулось сразу, а не на следующем старте).</summary>
    Task ReseedBuiltInAsync(CancellationToken ct = default);
}
