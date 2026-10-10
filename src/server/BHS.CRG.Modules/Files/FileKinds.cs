using System.IO.Compression;

namespace BHS.CRG.Modules.Files;

/// <summary>
/// Вид приложенного файла — по СОДЕРЖИМОМУ, а не по заголовку, который прислал клиент, и не по
/// имени (issue #1265).
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
/// <para>⚠️ Проверяется ПРИЗНАК вида, а не целость файла: подпись и версия у PDF, оглавление у
/// офисного файла. Файл, собранный нарочно под признак, вид получит — и дальше не откроется или не
/// распознается, о чём скажет уже то место, которое его читает.</para>
///
/// <para>Лежит в контрактах: файл счёта принимает модуль, а на ядро он не ссылается. Пока этим
/// определением пользуется только файл счёта. Что о виде известно сверх признака — чем он
/// показывается, как читается, предел размера, — держит реестр, <see cref="FileKindCatalog" />.</para>
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
    public const string Bmp = "image/bmp";
    public const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string Docx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string Xls = "application/vnd.ms-excel";

    /// <summary>Сколько первых байт нужно, чтобы узнать вид по подписи.</summary>
    public const int HeadBytes = 64;

    /// <summary>
    /// Вид по началу файла — для тех, кого выдаёт подпись: PDF и изображения. Офисный файл по началу
    /// не узнать (<c>.xlsx</c> и <c>.docx</c> — одинаковые архивы, <c>.xls</c> — контейнер, общий со
    /// старым Word): ему нужен файл целиком, см. <see cref="DetectAsync" />.
    /// </summary>
    public static string Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(PngSign)) return Png;
        if (head.StartsWith(JpegSign)) return Jpeg;
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return Gif;
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return WebP;
        // У BMP подпись из двух букв — слишком мало, чтобы верить ей одной: с «BM» начинается и
        // обычный текст. Следом за размером стоят четыре зарезервированных нуля.
        if (head.Length >= 14 && head.StartsWith("BM"u8) && head[6..10].SequenceEqual(FourZeros)) return Bmp;
        return IsPdf(head) ? Pdf : Unknown;
    }

    /// <summary>
    /// Вид по файлу целиком. Поток обязан уметь перемотку: у офисного файла читается оглавление, а
    /// оно в конце. Позицию не возвращает — поток после этого открывают заново.
    /// </summary>
    public static async Task<string> DetectAsync(Stream content, CancellationToken ct = default)
    {
        var head = new byte[HeadBytes];
        var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        var start = head.AsMemory(0, read);

        if (!content.CanSeek) return Detect(start.Span);
        if (start.Span.StartsWith(ZipSign)) return await OfficeInZipAsync(content, ct);
        if (start.Span.StartsWith(OleSign)) return await ExcelInOleAsync(content, ct);
        return Detect(start.Span);
    }

    /// <summary>
    /// С каким видом ОТДАВАТЬ файл: по его началу, а у офисного — по записи, сделанной при загрузке.
    ///
    /// <para>Запись вида у файла, приложенного до issue #1265, — заголовок клиента. Поэтому то, что
    /// узнаётся по подписи, узнаётся заново при каждой отдаче: запись при этом не читается вовсе,
    /// что бы в ней ни стояло. Офисный файл по началу не узнать, а хранилище отдаёт поток без
    /// перемотки — ему верим записи, но только если начало файла ей не противоречит: «таблица» по
    /// записи обязана начинаться как архив.</para>
    /// </summary>
    public static string Served(ReadOnlySpan<byte> head, string? recorded)
    {
        if (head.StartsWith(ZipSign))
            return Same(recorded, Xlsx) ? Xlsx : Same(recorded, Docx) ? Docx : Unknown;
        if (head.StartsWith(OleSign))
            return Same(recorded, Xls) ? Xls : Unknown;
        return Detect(head);
    }

    /// <summary>
    /// Записанный вид — к известному. Нужен там, где файла под рукой нет (кнопка «Распознать» решает
    /// по записи счёта): до issue #1265 записывался заголовок клиента, а один и тот же вид браузеры
    /// называли по-разному.
    /// </summary>
    public static string Recorded(string? stored) => stored?.Trim().ToLowerInvariant() switch
    {
        Pdf or "application/x-pdf" => Pdf,
        Png or "image/x-png" => Png,
        Jpeg or "image/jpg" or "image/pjpeg" => Jpeg,
        Gif => Gif,
        WebP => WebP,
        Bmp or "image/x-ms-bmp" => Bmp,
        Xlsx => Xlsx,
        Docx => Docx,
        Xls => Xls,
        _ => Unknown,
    };

    /// <summary>
    /// «%PDF-1.7» в самом начале. Перед подписью допустимы только пробельные байты и метка порядка
    /// байтов: читающие программы находят подпись и глубже, но «где-то в первом килобайте» делает
    /// PDF из любой страницы, в тексте которой эти пять букв встретились.
    /// </summary>
    private static bool IsPdf(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(Utf8Bom)) head = head[3..];
        head = head.TrimStart(" \t\r\n"u8);
        return head.Length >= 8 && head.StartsWith("%PDF-"u8)
            && char.IsAsciiDigit((char)head[5]) && head[6] == '.' && char.IsAsciiDigit((char)head[7]);
    }

    /// <summary>
    /// Архив — офисный файл, если в его оглавлении есть главная часть книги или документа. Имя файла
    /// не спрашивается: архив с расширением <c>.xlsx</c> таблицей не становится.
    /// </summary>
    private static async Task<string> OfficeInZipAsync(Stream content, CancellationToken ct)
    {
        try
        {
            content.Position = 0;
            await using var zip = await ZipArchive.CreateAsync(
                content, ZipArchiveMode.Read, leaveOpen: true, entryNameEncoding: null, ct);
            if (zip.GetEntry("xl/workbook.xml") is not null) return Xlsx;
            if (zip.GetEntry("word/document.xml") is not null) return Docx;
            return Unknown;
        }
        catch (InvalidDataException)
        {
            // Подпись архива есть, оглавления нет: файл обрезан или архивом только назвался.
            return Unknown;
        }
    }

    /// <summary>
    /// Контейнер OLE — старый Excel, если в нём есть поток «Workbook» и нет потока «WordDocument»
    /// (документ Word со вставленной таблицей несёт оба). Имена потоков лежат в каталоге контейнера
    /// в UTF-16; каталог может стоять где угодно, поэтому просматривается весь файл.
    /// </summary>
    private static async Task<string> ExcelInOleAsync(Stream content, CancellationToken ct)
    {
        content.Position = 0;
        var workbook = false;
        var buffer = new byte[81920];
        var kept = 0;
        while (true)
        {
            var read = await content.ReadAsync(buffer.AsMemory(kept), ct);
            if (read == 0) break;
            var window = buffer.AsSpan(0, kept + read);
            if (window.IndexOf(WordStream) >= 0) return Unknown;
            workbook = workbook || window.IndexOf(WorkbookStream) >= 0;

            // Хвост переносится в начало следующего куска: имя потока могло лечь на границу.
            kept = Math.Min(window.Length, WordStream.Length - 1);
            window[^kept..].CopyTo(buffer);
        }
        return workbook ? Xls : Unknown;
    }

    private static bool Same(string? recorded, string kind) =>
        string.Equals(recorded?.Trim(), kind, StringComparison.OrdinalIgnoreCase);

    private static ReadOnlySpan<byte> PngSign => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JpegSign => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> ZipSign => [0x50, 0x4B, 0x03, 0x04];
    private static ReadOnlySpan<byte> OleSign => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];
    private static ReadOnlySpan<byte> FourZeros => [0, 0, 0, 0];

    // Имена потоков в UTF-16LE, как они записаны в каталоге контейнера.
    private static ReadOnlySpan<byte> WorkbookStream =>
        [(byte)'W', 0, (byte)'o', 0, (byte)'r', 0, (byte)'k', 0, (byte)'b', 0, (byte)'o', 0, (byte)'o', 0, (byte)'k', 0];

    private static ReadOnlySpan<byte> WordStream =>
        [(byte)'W', 0, (byte)'o', 0, (byte)'r', 0, (byte)'d', 0, (byte)'D', 0, (byte)'o', 0, (byte)'c', 0,
         (byte)'u', 0, (byte)'m', 0, (byte)'e', 0, (byte)'n', 0, (byte)'t', 0];
}
