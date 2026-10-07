namespace BHS.CRG.Application.Recognition;

/// <summary>
/// Что вид профиля означает для UI (issue #408). Отдаётся с сервера, чтобы фронт НЕ знал частных
/// случаев видов: показывать ли редактор скалярных полей, колонок, флагов формы и какие поля
/// защищены — всё выводится отсюда, а не из зашитых на клиенте условий вида.
/// </summary>
/// <param name="Scope">Куда привязывается профиль этого вида: "File" (набор целиком — штамп/обложка/
/// счёт) или "PageGroup" (группа листов — таблицы). «Есть табличная часть» для этого не годится:
/// у счёта она есть, но привязывается он к набору.</param>
/// <param name="Module">Код владельца вида: модуль или «core» (issue #1075). null — вид не объявлен
/// никем.</param>
/// <param name="ModuleTitle">Название владельца — заголовок группы на экране.</param>
public record RecognitionKindInfo(
    string Kind,
    string Label,
    bool SupportsShape,
    bool HasScalarFields,
    bool IsTabular,
    IReadOnlyList<string> SystemFieldNames,
    string Scope = "File",
    string? Module = null,
    string? ModuleTitle = null);

/// <summary>Профиль распознавания для UI. Признак «системное поле» приходит списком имён из
/// дескриптора вида — в данных профиля он не хранится (иначе снимался бы импортом).</summary>
public record RecognitionProfileDto(
    Guid Id,
    string Name,
    string? Code,
    string Kind,
    IReadOnlyList<RecognitionProfileField> Fields,
    IReadOnlyList<RecognitionProfileField> RowColumns,
    RecognitionTableShape? Shape,
    bool IsBuiltIn,
    bool IsModified,
    bool BuiltInOutdated,
    RecognitionKindInfo KindInfo,
    // Владелец ПРОФИЛЯ, а не вида (issue #1075): по нему экран складывает профили в группы.
    string? Module = null,
    string? ModuleTitle = null);

/// <summary>
/// Профиль, которого на этом экземпляре не предлагают: его модуль выключен (issue #1075).
///
/// <para>Отдаётся отдельно и без содержимого. Экрану он нужен в двух местах: сказать «скрыто столько-то
/// профилей — и почему», и назвать привязку набора, сделанную, пока модуль был включён. Без него такая
/// привязка выглядела бы как «встроенный профиль»: идентификатор стоит, а в списке его нет.</para>
/// </summary>
public record HiddenRecognitionProfileDto(
    Guid Id,
    string Name,
    string Kind,
    string KindLabel,
    string? Module,
    string? ModuleTitle);
