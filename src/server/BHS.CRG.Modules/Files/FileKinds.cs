namespace BHS.CRG.Modules.Files;

/// <summary>
/// Вид приложенного файла — по СОДЕРЖИМОМУ, а не по заголовку, который прислал клиент (issue #1265).
///
/// <para>Заголовку верить нельзя по двум причинам. Он бывает пуст: с машины без Office браузер шлёт
/// файл Excel как <c>application/octet-stream</c> или вовсе без типа. И он бывает чужим: записанный
/// как есть, он потом возвращается при отдаче, и браузер открывает файл тем, чем его назвали, а не
/// тем, что он есть.</para>
///
/// <para>Перечень закрыт: отдаётся только вид, названный здесь, всё прочее —
/// <see cref="Unknown" />, то есть «только скачать». Новый вид вписывают сюда, а не пропускают
/// «раз браузер сам разберётся».</para>
///
/// <para>Лежит в контрактах: файл счёта принимает модуль, а на ядро он не ссылается; реестр
/// читаемых видов ядра (issue #1266) опирается на то же определение.</para>
/// </summary>
public static class FileKinds
{
    /// <summary>Вид не определён: файл хранится и скачивается, но не показывается и не распознаётся.</summary>
    public const string Unknown = "application/octet-stream";

    public const string Pdf = "application/pdf";
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string WebP = "image/webp";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Xls = "application/vnd.ms-excel";

    /// <summary>
    /// Сколько первых байт нужно определению. Столько, а не десяток: у PDF подпись вправе стоять не
    /// с первого байта — читающие программы ищут её в первом килобайте, и сканеры этим пользуются.
    /// </summary>
    public const int HeadBytes = 1024;

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
        { Pdf, Png, Jpeg, Gif, WebP, Xlsx, Docx, Xls };

    /// <summary>
    /// Вид по началу файла и имени. Имя нужно только там, где содержимое одно на несколько видов:
    /// <c>.xlsx</c> и <c>.docx</c> — оба архивы ZIP, <c>.xls</c> — контейнер OLE, общий со старым
    /// Word. Содержимое при этом главнее: PDF с именем «счёт.xlsx» — PDF, а архив с именем
    /// «счёт.pdf» — неизвестный файл.
    /// </summary>
    public static string Detect(ReadOnlySpan<byte> head, string? fileName)
    {
        if (head.StartsWith(PngSign)) return Png;
        if (head.StartsWith(JpegSign)) return Jpeg;
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return Gif;
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return WebP;

        var extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (head.StartsWith(ZipSign))
            return extension switch { ".xlsx" => Xlsx, ".docx" => Docx, _ => Unknown };
        if (head.StartsWith(OleSign))
            return extension == ".xls" ? Xls : Unknown;

        // Последним: подпись ищется в глубину, и на ней не должен сработать архив, внутри которого
        // эти пять байт встретились случайно.
        var window = head.Length > HeadBytes ? head[..HeadBytes] : head;
        return window.IndexOf("%PDF-"u8) >= 0 ? Pdf : Unknown;
    }

    /// <summary>Тот же ответ по потоку: читает начало и НЕ возвращает позицию — поток открывают заново.</summary>
    public static async Task<string> DetectAsync(Stream content, string? fileName, CancellationToken ct = default)
    {
        var head = new byte[HeadBytes];
        var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        return Detect(head.AsSpan(0, read), fileName);
    }

    /// <summary>
    /// С каким видом отдавать файл, у которого вид уже ЗАПИСАН. Запись могла прийти до issue #1265,
    /// то есть из заголовка клиента: известный вид отдаётся как записан, любой другой —
    /// <see cref="Unknown" />. Пересчёта по содержимому у старых записей нет: он потребовал бы
    /// перечитать хранилище целиком, а закрывает ровно то же — чужой вид наружу не уходит.
    /// </summary>
    public static string Served(string? stored) =>
        stored is not null && Known.Contains(stored) ? stored.ToLowerInvariant() : Unknown;

    private static ReadOnlySpan<byte> PngSign => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JpegSign => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> ZipSign => [0x50, 0x4B, 0x03, 0x04];
    private static ReadOnlySpan<byte> OleSign => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
}
