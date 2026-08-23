namespace BHS.CRG.Application.Backup;

/// <summary>
/// Что кладём в копию (issue #833).
///
/// Различие не в объёме, а в назначении: <see cref="Configuration" /> — страховка настройки
/// системы (то, чем копия была до #833), <see cref="Full" /> — переезд и восстановление после
/// сбоя, ради которых эпик и заведён. Полная тяжелее в разы: в неё едут файлы наборов данных с
/// разобранным кэшем и выпущенные PDF.
/// </summary>
public enum BackupScope
{
    /// <summary>Только настройка системы и справочники.</summary>
    Configuration,

    /// <summary>Настройка плюс проектная работа: стройки, комплекты, документы, наборы данных.</summary>
    Full,
}

/// <summary>Раздел копии и число записей в нём — «состав» в списке копий.</summary>
public sealed record BackupSectionCount(string Label, int Count);

/// <summary>
/// Паспорт копии — отдельная МАЛЕНЬКАЯ запись <c>summary.json</c> в начале архива (issue #831).
///
/// Зачем отдельно от манифеста. Список копий показывает дату, версию и состав, а манифест — это
/// весь перенос целиком: с картинками в base64 он тянет на сотни мегабайт, и разбирать его ради
/// одной строки списка нельзя. Паспорт же читается по оглавлению zip, не касаясь остального
/// архива, и поэтому одинаково дёшев и для копии в 5 МБ, и для копии в 3 ГБ.
///
/// Кладётся ВНУТРЬ архива, а не файлом рядом: копию увозят и приносят одним файлом, и спутник
/// потерялся бы при первом же переносе — а вместе с ним и всё, что список умеет сказать о чужой
/// копии.
/// </summary>
public sealed record BackupSummary(
    int SchemaVersion,
    string AppVersion,
    DateTimeOffset CreatedAt,
    int BlobCount,
    IReadOnlyList<BackupSectionCount> Sections,
    /// <summary>Полная копия или только настройка (issue #833). У копий до 0.143.0 поля нет —
    /// читается как <c>false</c>, что для них правда.</summary>
    bool IncludesProjectData = false,
    /// <summary>
    /// Что экспорт пропустил и почему — например шаблоны, потерявшие свой тип документа. Список
    /// живёт в паспорте, а не только в журнале: пропуск при снятии копии обнаруживается при
    /// восстановлении, то есть после аварии, а прочитать его надо раньше.
    /// </summary>
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// Строка списка копий. <paramref name="Problem" /> — не техническая деталь, а единственный честный
/// ответ про файл, который лежит в каталоге, но паспорта не имеет или не читается: спрятать такой
/// файл значило бы показать администратору пустой список там, где он только что положил копию.
/// </summary>
public sealed record BackupFileInfo(
    string FileName,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    string? AppVersion,
    int? SchemaVersion,
    int? BlobCount,
    IReadOnlyList<BackupSectionCount>? Sections,
    string? Problem,
    /// <summary>Полная копия (с проектными данными) или только настройка — issue #833.</summary>
    bool IncludesProjectData = false,
    IReadOnlyList<string>? Warnings = null);
