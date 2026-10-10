namespace BHS.CRG.Modules.Files;

/// <summary>Чем приложенный файл показывается рядом с формой.</summary>
public enum FileView
{
    /// <summary>Показать нечем: файл только скачивается.</summary>
    None,
    Pdf,
    Image,
}

/// <summary>Как файл читается распознаванием.</summary>
public enum FileReading
{
    /// <summary>Не читается: приложить можно, распознать — нет.</summary>
    None,

    /// <summary>Как есть: файл уходит движку распознавания без преобразования.</summary>
    AsIs,

    /// <summary>
    /// Через читаемый образ: из файла сначала строится PDF с текстовым слоем (эпик #1264), и
    /// движку уходит он. Образ строит и хранит ядро — модуль спрашивает его портом
    /// <see cref="BHS.CRG.Modules.Ports.IModuleRenditions" />.
    /// </summary>
    Rendition,
}

/// <summary>Один вид файла: всё, что о нём знают ядро, модули и экран.</summary>
/// <param name="Mime">Каким типом файл хранится и отдаётся — одно из значений <see cref="FileKinds" />.</param>
/// <param name="Label">Как вид называется человеку: в перечне «распознаются …» и в отказе.</param>
/// <param name="Extensions">Расширения с точкой — для выбора файла. В определении вида НЕ участвуют:
/// вид даёт содержимое (<see cref="FileKinds.DetectAsync" />).</param>
/// <param name="Aliases">Как ещё этот вид называют браузеры и системы («image/pjpeg» у JPEG). Нужны
/// там, где о файле известно только название типа, а самого файла под рукой нет: запись в счёте,
/// сделанная до issue #1265, и отсев на экране до отправки.</param>
public sealed record FileKind(
    string Mime, string Label, IReadOnlyList<string> Extensions, FileView View, FileReading Reading,
    IReadOnlyList<string> Aliases);

/// <summary>
/// Реестр видов файлов (issue #1266) — один источник для ядра, модулей и экрана.
///
/// <para>До него перечень «что система умеет читать» был записан в трёх местах и разошёлся: выбор
/// файла в панели счёта пропускал любое изображение, модуль счетов читал PDF, PNG и JPEG, а движки
/// распознавания брали ещё WebP и GIF. С офисными файлами мест стало бы четыре.</para>
///
/// <para><b>Что читается «как есть» — PDF, PNG и JPEG, и это выбор, а не пропуск.</b> Сюда входит
/// только то, что принимает КАЖДЫЙ движок: какой из них окажется в работе, решает настройка
/// экземпляра, и вид, который читает один движок из трёх, давал бы «у соседей распозналось, а у нас
/// нет». WebP и GIF движки по отдельности принимают, но не все и не наверняка, поэтому такой файл
/// показывается, но не распознаётся. Сводит одно с другим <c>FileKindCatalogTests</c>: вид «как
/// есть», от которого хоть один движок отказывается, — красный тест.</para>
///
/// <para>Лежит в контрактах рядом с <see cref="FileKinds" />: файл принимает модуль, а на ядро он
/// не ссылается. Экран получает реестр адресом <c>GET /api/files/kinds</c> и своих перечней не
/// держит.</para>
/// </summary>
public static class FileKindCatalog
{
    /// <summary>
    /// Предел размера — один на все виды и равен пределу вложения ядра: от него считается предел
    /// тела запроса, и файл больше до проверки не дошёл бы вовсе (сверяет <c>ScanLimitsAgreeTests</c>).
    /// Своего предела у вида нет нарочно: поле, которое никто не проверяет, обещало бы настройку.
    /// </summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    public static readonly IReadOnlyList<FileKind> All =
    [
        new(FileKinds.Pdf, "PDF", [".pdf"], FileView.Pdf, FileReading.AsIs, ["application/x-pdf"]),
        new(FileKinds.Png, "PNG", [".png"], FileView.Image, FileReading.AsIs, ["image/x-png"]),
        new(FileKinds.Jpeg, "JPEG", [".jpg", ".jpeg"], FileView.Image, FileReading.AsIs, ["image/jpg", "image/pjpeg"]),
        new(FileKinds.Gif, "GIF", [".gif"], FileView.Image, FileReading.None, []),
        new(FileKinds.WebP, "WebP", [".webp"], FileView.Image, FileReading.None, []),
        new(FileKinds.Bmp, "BMP", [".bmp"], FileView.Image, FileReading.None, ["image/x-ms-bmp"]),
        // Офисные виды первой версии — те, что проверены пробой на настоящих счетах. Старый Word,
        // RTF и таблицы OpenDocument сюда не входят, пока не проверены так же: до тех пор они
        // «файл другого вида».
        new(FileKinds.Xlsx, "Excel", [".xlsx"], FileView.None, FileReading.Rendition, []),
        new(FileKinds.Xls, "Excel", [".xls"], FileView.None, FileReading.Rendition, []),
        new(FileKinds.Docx, "Word", [".docx"], FileView.None, FileReading.Rendition, []),
    ];

    private static readonly Dictionary<string, FileKind> ByMime =
        All.ToDictionary(kind => kind.Mime, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, FileKind> ByName = All
        .SelectMany(kind => kind.Aliases.Prepend(kind.Mime), (kind, name) => (name, kind))
        .ToDictionary(pair => pair.name, pair => pair.kind, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Вид по названию типа, каким бы из известных имён его ни назвали. Для названий, пришедших
    /// СНАРУЖИ (запись заголовка клиента); о виде, который отдал сам сервер, спрашивают
    /// <see cref="Find" /> — он синонимов не принимает.
    /// </summary>
    public static FileKind? Named(string? name) =>
        name is not null && ByName.TryGetValue(name.Trim(), out var kind) ? kind : null;

    /// <summary>Вид по его типу; <c>null</c> — реестр такого не знает (в том числе <see cref="FileKinds.Unknown" />).</summary>
    public static FileKind? Find(string? mime) =>
        mime is not null && ByMime.TryGetValue(mime.Trim(), out var kind) ? kind : null;

    /// <summary>
    /// Что распознаётся: и «как есть», и через читаемый образ (issue #1270).
    ///
    /// <para>⚠️ Перечень отвечает на вопрос «можно ли распознать файл такого вида», а не «что
    /// принимает движок»: офисный файл движку не уходит никогда — уходит его образ. Кто отдаёт
    /// файл движку, обязан сначала спросить образ (<c>IModuleRenditions.EnsureAsync</c>).</para>
    /// </summary>
    public static readonly IReadOnlyList<FileKind> Recognized =
        [.. All.Where(kind => kind.Reading != FileReading.None)];

    /// <summary>Спрашивает перечень выше, а не повторяет его условие: «что распознаётся» сказано один раз.</summary>
    public static bool IsRecognized(string? mime) => Find(mime) is { } kind && Recognized.Contains(kind);

    /// <summary>
    /// Перечень словами: «PDF, PNG и JPEG» или «PDF, PNG или JPEG». Одинаково названные виды
    /// (два Excel) называются один раз.
    /// </summary>
    public static string Words(IEnumerable<FileKind> kinds, string last = "и")
    {
        var labels = kinds.Select(kind => kind.Label).Distinct().ToList();
        return labels.Count switch
        {
            0 => "",
            1 => labels[0],
            _ => $"{string.Join(", ", labels.Take(labels.Count - 1))} {last} {labels[^1]}",
        };
    }
}
