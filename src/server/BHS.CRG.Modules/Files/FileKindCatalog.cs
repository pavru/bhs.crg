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
    /// Через читаемый образ: из файла сначала строится PDF с текстовым слоем (эпик #1264). Пока
    /// образ не строится (issue #1268–#1270), такой файл распознаванию недоступен.
    /// </summary>
    Rendition,
}

/// <summary>Один вид файла: всё, что о нём знают ядро, модули и экран.</summary>
/// <param name="Mime">Каким типом файл хранится и отдаётся — одно из значений <see cref="FileKinds" />.</param>
/// <param name="Label">Как вид называется человеку: в перечне «распознаются …» и в отказе.</param>
/// <param name="Extensions">Расширения с точкой — для выбора файла. В определении вида НЕ участвуют:
/// вид даёт содержимое (<see cref="FileKinds.DetectAsync" />).</param>
/// <param name="MaxBytes">Наибольший размер файла этого вида.</param>
public sealed record FileKind(
    string Mime, string Label, IReadOnlyList<string> Extensions, FileView View, FileReading Reading, long MaxBytes);

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
    /// </summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    public static readonly IReadOnlyList<FileKind> All =
    [
        new(FileKinds.Pdf, "PDF", [".pdf"], FileView.Pdf, FileReading.AsIs, MaxBytes),
        new(FileKinds.Png, "PNG", [".png"], FileView.Image, FileReading.AsIs, MaxBytes),
        new(FileKinds.Jpeg, "JPEG", [".jpg", ".jpeg"], FileView.Image, FileReading.AsIs, MaxBytes),
        new(FileKinds.Gif, "GIF", [".gif"], FileView.Image, FileReading.None, MaxBytes),
        new(FileKinds.WebP, "WebP", [".webp"], FileView.Image, FileReading.None, MaxBytes),
        new(FileKinds.Bmp, "BMP", [".bmp"], FileView.Image, FileReading.None, MaxBytes),
        // Офисные виды первой версии — те, что проверены пробой на настоящих счетах. Старый Word,
        // RTF и таблицы OpenDocument сюда не входят, пока не проверены так же: до тех пор они
        // «файл другого вида».
        new(FileKinds.Xlsx, "Excel", [".xlsx"], FileView.None, FileReading.Rendition, MaxBytes),
        new(FileKinds.Xls, "Excel", [".xls"], FileView.None, FileReading.Rendition, MaxBytes),
        new(FileKinds.Docx, "Word", [".docx"], FileView.None, FileReading.Rendition, MaxBytes),
    ];

    private static readonly Dictionary<string, FileKind> ByMime =
        All.ToDictionary(kind => kind.Mime, StringComparer.OrdinalIgnoreCase);

    /// <summary>Вид по его типу; <c>null</c> — реестр такого не знает (в том числе <see cref="FileKinds.Unknown" />).</summary>
    public static FileKind? Find(string? mime) =>
        mime is not null && ByMime.TryGetValue(mime.Trim(), out var kind) ? kind : null;

    /// <summary>
    /// Что распознаётся СЕЙЧАС. Только «как есть»: вид с читаемым образом войдёт сюда, когда образ
    /// начнёт строиться (issue #1270), а до тех пор назвать его читаемым значило бы обещать
    /// распознавание, которого нет.
    /// </summary>
    public static readonly IReadOnlyList<FileKind> Recognized =
        [.. All.Where(kind => kind.Reading == FileReading.AsIs)];

    public static bool IsRecognized(string? mime) => Find(mime) is { Reading: FileReading.AsIs };

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
