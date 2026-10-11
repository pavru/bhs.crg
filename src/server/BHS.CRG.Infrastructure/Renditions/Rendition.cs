namespace BHS.CRG.Infrastructure.Renditions;

/// <summary>
/// Почему читаемого образа нет. Повтор лечит только <see cref="Unavailable" />: остальное — свойство
/// самого файла, и та же попытка даст тот же отказ.
/// </summary>
public enum RenditionRefusal
{
    /// <summary>Файл защищён паролем.</summary>
    Protected,

    /// <summary>Не тот формат: вид файла системе неизвестен или не читается вовсе.</summary>
    WrongFormat,

    /// <summary>Назвался офисным файлом, а прочитать его нельзя.</summary>
    Corrupted,

    /// <summary>В файле нет ни одной заполненной ячейки, ни строчки текста.</summary>
    Empty,

    /// <summary>Не уложился в бюджет: размер, распакованный размер, число ячеек, страниц, время.</summary>
    TooLarge,

    /// <summary>Конвертер файл взял, а образа не получилось — или получился не тот.</summary>
    Failed,

    /// <summary>Конвертер занят или молчит. Единственный отказ, который лечится повтором.</summary>
    Unavailable,

    /// <summary>
    /// Конвертера у экземпляра нет или его адрес неверен. Повтором не лечится — только настройкой:
    /// названный «недоступным», такой отказ звал бы повторять без конца.
    /// </summary>
    NotSetUp,
}

/// <summary>
/// Офисный файл, из которого строится образ. Свой перечень, а не тип из реестра видов: реестр лежит
/// в контрактах модулей, а на них этот слой не ссылается — вид по содержимому определяет тот, кто
/// службу зовёт.
/// </summary>
public enum OfficeFormat
{
    Xlsx,
    Xls,
    Docx,
}

/// <summary>
/// Ответ на вопрос «дай читаемый образ этого файла» (issue #1268). Их три, и четвёртого нет: пустого
/// PDF как значения не существует — файл, из которого образ не получился, получает отказ с причиной.
/// </summary>
public abstract record Rendition
{
    private Rendition() { }

    /// <summary>Читается как есть: PDF или изображение. Образ ему не нужен.</summary>
    /// <param name="Mime">Вид файла по содержимому — одно из значений <c>FileKinds</c>.</param>
    public sealed record AsIs(string Mime) : Rendition;

    /// <summary>Образ построен: PDF с текстовым слоем, прошедший постусловия.</summary>
    /// <param name="Pdf">Сам образ.</param>
    /// <param name="Pages">Сколько в нём страниц.</param>
    /// <param name="Notes">Пометки для человека: что в образе не так, хотя он и годен. Едут вместе
    /// с образом и показываются рядом с ним — образ с пометкой, показанный без неё, выглядел бы
    /// точной копией файла.</param>
    public sealed record Built(byte[] Pdf, int Pages, IReadOnlyList<string> Notes) : Rendition
    {
        /// <summary>
        /// Чем построен образ: «gotenberg 8.37.0» (issue #1269). <c>null</c> — конвертер себя не
        /// назвал; образ от этого хуже не стал, и отказом это не считается.
        /// </summary>
        public string? Converter { get; init; }
    }

    /// <summary>Образа нет.</summary>
    /// <param name="Kind">Вид причины — по нему решают, предлагать ли повтор.</param>
    /// <param name="Reason">Причина словами, для человека. Пишет служба: ответ конвертера сюда не
    /// попадает никогда — в нём может оказаться содержимое файла.</param>
    public sealed record Refused(RenditionRefusal Kind, string Reason) : Rendition
    {
        public bool RetryHelps => Kind.RetryHelps();
    }
}

/// <summary>
/// Бюджеты читаемого образа. Каждое число — предохранитель, а не настройка: счёт занимает десятки
/// килобайт и одну-две страницы, и файл, упёршийся в любой из пределов, счётом не является.
/// </summary>
public static class RenditionLimits
{
    /// <summary>
    /// Предел офисного файла — ниже общего предела вложения (50 МБ): тот рассчитан на скан, а
    /// таблица такого размера — это сотни тысяч строк, и строить из неё картинку для чтения незачем.
    /// </summary>
    public const long OfficeMaxBytes = 20L * 1024 * 1024;

    /// <summary>Записей в архиве <c>.xlsx</c> или <c>.docx</c>. У счёта их десятка два.</summary>
    public const int ArchiveMaxEntries = 2000;

    /// <summary>Сколько байт архив вправе занять распакованным.</summary>
    public const long ArchiveMaxUnpackedBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Во сколько раз архив вправе раздуться. Разметка таблицы сжимается хорошо, и маленький файл
    /// честно раздувается в десятки раз — поэтому отношение спрашивается только выше
    /// <see cref="ArchiveRatioFloorBytes" />.
    /// </summary>
    public const int ArchiveMaxRatio = 100;

    public const long ArchiveRatioFloorBytes = 10L * 1024 * 1024;

    /// <summary>Заполненных ячеек в книге.</summary>
    public const int MaxCells = 200_000;

    /// <summary>
    /// Ячеек, пройденных при чтении книги, считая пустые. Пропуски между далёкими ячейками
    /// разборщик отдаёт пустыми строками во всю ширину листа: файл в несколько килобайт с ячейками
    /// по углам листа — это семнадцать миллиардов шагов.
    /// </summary>
    public const long MaxVisitedCells = 5_000_000;

    /// <summary>Сколько разных слов книги ищется в тексте образа (см. <c>RenditionPostconditions</c>).</summary>
    public const int MaxCheckedWords = 2000;

    /// <summary>Страниц в образе.</summary>
    public const int MaxPages = 100;

    /// <summary>Готовый PDF не больше обычного вложения: хранится и отдаётся он так же.</summary>
    public const long MaxPdfBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Доля слов из ячеек книги, найденных в текстовом слое образа. Ниже первого порога образ —
    /// не образ этого файла, и это отказ; между порогами он годен, но несёт пометку.
    ///
    /// <para>Подобраны на настоящих счетах (проба эпика #1264, двенадцать книг): у здоровых —
    /// 99,4–100 %, у книги с ячейкой, не поместившейся внизу листа, — 94 %, и данные счёта в ней
    /// целы. Доля высока и у обрезанной потому, что слово считается найденным, где бы оно ни
    /// стояло, а слова потерянного хвоста встречаются и выше; поэтому порог пометки стоит вплотную
    /// к здоровым.</para>
    /// </summary>
    public const double CoverageRefuseBelow = 0.5;

    public const double CoverageNoteBelow = 0.98;

    /// <summary>Сколько файл ждёт своей очереди к конвертеру, прежде чем получить «занят».</summary>
    public static readonly TimeSpan GateWait = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Что вид отказа значит для того, кто его получил. Функции от вида, а не свойства ответа: вид
/// хранится в записи образа именем, и правило нужно тому, у кого на руках только оно.
///
/// <para>⚠️ Имена <see cref="RenditionRefusal" /> — формат хранения: они лежат в таблице образов.
/// Переименование члена делает старые записи нечитаемыми; сторож —
/// <c>Имена_видов_отказа_это_формат_хранения</c>.</para>
/// </summary>
public static class RenditionRefusalRules
{
    /// <summary>Повтор поможет: сервис занят или молчит, и тот же файл позже получит образ.</summary>
    public static bool RetryHelps(this RenditionRefusal kind) => kind == RenditionRefusal.Unavailable;

    /// <summary>
    /// Отказ запоминают (issue #1269) — чтобы не строить заново на каждый вопрос. Не запоминают
    /// то, что говорит не о файле:
    /// <list type="bullet">
    /// <item>о сервисе — занят, молчит, не настроен: запись «конвертера нет» пережила бы его
    /// настройку;</item>
    /// <item>о версии приложения — «файл другого вида»: какие виды читаются, решает реестр видов, и
    /// он растёт. Ответ этот дешёвый — по первым байтам файла, — и хранить его незачем.</item>
    /// </list>
    /// </summary>
    public static bool Remembered(this RenditionRefusal kind) =>
        kind is not (RenditionRefusal.Unavailable or RenditionRefusal.NotSetUp or RenditionRefusal.WrongFormat);
}
