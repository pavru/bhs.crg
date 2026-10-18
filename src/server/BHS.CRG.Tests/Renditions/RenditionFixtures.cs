using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BHS.CRG.Api.Renditions;
using BHS.CRG.Infrastructure.OfficeConversion;
using BHS.CRG.Infrastructure.Renditions;
using Microsoft.Extensions.Logging.Abstractions;
using NPOI.POIFS.FileSystem;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace BHS.CRG.Tests.Renditions;

/// <summary>
/// Из чего собраны тесты читаемого образа (issue #1268): подставной конвертер и синтетические файлы.
/// Настоящих счетов в репозитории нет.
///
/// <para>Служба проверяется ЧЕРЕЗ СВОЙ ВХОД — <see cref="RenditionService.BuildAsync" />, с
/// конвертером, подменённым на уровне HTTP. Так тест ломает то же место, что сломал бы настоящий
/// сервис, вернувший не то.</para>
/// </summary>
internal static class RenditionFixtures
{
    public delegate Task<HttpResponseMessage> Converter(HttpRequestMessage request, CancellationToken ct);

    /// <summary>Конвертер, которого звать не должны: файл обязан получить отказ раньше.</summary>
    public static readonly Converter MustNotBeCalled =
        (_, _) => throw new InvalidOperationException("Файл дошёл до конвертера, а должен был получить отказ до него.");

    public static Converter Returns(byte[] pdf) => (_, _) => Task.FromResult(PdfReply(pdf));

    public static Converter Answers(HttpStatusCode status, string body = "") =>
        (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

    /// <summary>Под каким именем файл ушёл конвертеру: по расширению тот выбирает, чем его открыть.</summary>
    public static string SentName(HttpRequestMessage request) =>
        Assert.Single(Assert.IsType<MultipartFormDataContent>(request.Content))
            .Headers.ContentDisposition!.FileName!.Trim('"');

    public static HttpResponseMessage PdfReply(byte[] pdf)
    {
        var content = new ByteArrayContent(pdf);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <param name="version">Что сервис отвечает на вопрос о своей версии; <c>null</c> — не
    /// отвечает (404). Вопрос этот до <paramref name="converter" /> не доходит: тот видит только
    /// преобразования, и считать или проверять в нём можно именно их.</param>
    /// <param name="onVersion">Свой ответ на вопрос о версии — когда тесту важно, КОГДА его задали.</param>
    public static OfficeConverterClient Client(
        Converter converter, string? baseUrl = "http://converter:3000", TimeSpan? timeout = null,
        TimeSpan? gateWait = null, string? version = null, Converter? onVersion = null, TimeSpan? markWait = null)
        => new(new Clients(converter, version, onVersion), new OfficeConverterOptions { BaseUrl = baseUrl },
            NullLogger<OfficeConverterClient>.Instance)
        {
            Timeout = timeout ?? OfficeConverterOptions.ClientTimeout,
            GateWait = gateWait ?? RenditionLimits.GateWait,
            MarkWait = markWait ?? TimeSpan.FromSeconds(2),
        };

    public static RenditionService Service(Converter converter, string? version = null) =>
        new(new OfficeRenditionBuilder(Client(converter, version: version), NullLogger<OfficeRenditionBuilder>.Instance));

    public static Task<Rendition> BuildAsync(this RenditionService service, byte[] file) =>
        service.BuildAsync(new MemoryStream(file), PageProfile.Default, CancellationToken.None);

    // ── Файлы ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Книга из строк текста, по строке на ячейку. Текст — латиницей: PDF для сверки тесты собирают
    /// сами, а в стандартных шрифтах PDF кириллицы нет. Кириллицу держит пара эталонов из
    /// <c>deploy/</c>, полученная от настоящего конвертера.
    /// </summary>
    public static byte[] Workbook(params string[] cells) => Workbook(book =>
    {
        var sheet = book.CreateSheet("Sheet");
        for (var row = 0; row < cells.Length; row++) sheet.CreateRow(row).CreateCell(0).SetCellValue(cells[row]);
    });

    /// <param name="ownLayout">
    /// Книга задаёт раскладку сама (вписывание на каждом листе), и профиль печати её не трогает (issue
    /// #1453). По умолчанию так: тесты сверки проверяют сверку, а не раскладку, и конвертер у них
    /// получает тот же файл, что собран здесь. Раскладку проверяют с <c>ownLayout: false</c>.
    /// </param>
    public static byte[] Workbook(Action<IWorkbook> fill, bool old = false, bool ownLayout = true)
    {
        using IWorkbook book = old ? new NPOI.HSSF.UserModel.HSSFWorkbook() : new XSSFWorkbook();
        fill(book);
        if (ownLayout && !old)
            for (var i = 0; i < book.NumberOfSheets; i++) book.GetSheetAt(i).FitToPage = true;
        using var file = new MemoryStream();
        book.Write(file, leaveOpen: true);
        return file.ToArray();
    }

    /// <summary>
    /// Книга Excel 5.0/95 (BIFF5) — формат, в котором два из трёх настоящих <c>.xls</c> обзора счетов
    /// (issue #1457). NPOI такую не пишет и не читает, поэтому записи собраны руками: начало книги,
    /// кодировка, лист, по желанию — область печати, затем лист с текстовыми ячейками. Так же собранную
    /// книгу читают и разборщик ячеек, и настоящий конвертер — и область он исполняет (проверено живьём).
    /// </summary>
    /// <param name="printArea">Область печати листа: строки и колонки с нуля, границы включительно.</param>
    /// <param name="onSheet">Ссылка области — на лист (ptgArea3d, как пишет Excel) или простая (ptgArea):
    /// обе LibreOffice исполняет.</param>
    /// <param name="sheetName">Имя листа — байтами кодировки 1251, как пишут его русские программы.</param>
    public static byte[] Biff5Workbook((int Row, int Column, string Text)[] cells,
        (int Top, int Left, int Bottom, int Right)? printArea = null, bool onSheet = true, string sheetName = "Sheet")
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cyrillic = Encoding.GetEncoding(1251);
        static byte[] Record(ushort id, params byte[][] parts)
        {
            var body = parts.SelectMany(part => part).ToArray();
            return [.. BitConverter.GetBytes(id), .. BitConverter.GetBytes((ushort)body.Length), .. body];
        }
        static byte[] U16(int value) => BitConverter.GetBytes((ushort)value);
        static byte[] Begin(int type) => Record(0x0809, U16(0x0500), U16(type), U16(0), U16(0));
        var end = Record(0x000A);

        var sheet = new List<byte>(Begin(0x0010));
        sheet.AddRange(Record(0x0200, U16(0), U16(cells.Max(c => c.Row) + 1), U16(0), U16(cells.Max(c => c.Column) + 1), U16(0)));
        foreach (var (row, column, text) in cells)
            sheet.AddRange(Record(0x0204, U16(row), U16(column), U16(0), U16(text.Length), Encoding.ASCII.GetBytes(text)));
        sheet.AddRange(end);

        // Встроенное имя Print_Area (0x06) листа 1. Формула — знак ссылки и сама ссылка в раскладке BIFF5:
        // у ptgArea3d — лист (2+8+2+2), затем у обеих строки (2+2) и колонки (1+1).
        byte[] formula = printArea is { } area
            ? [onSheet ? (byte)0x3B : (byte)0x25, .. onSheet ? [.. U16(0xFFFF), .. new byte[8], .. U16(0), .. U16(0)] : Array.Empty<byte>(),
                .. U16(area.Top), .. U16(area.Bottom), (byte)area.Left, (byte)area.Right]
            : [];
        var name = formula.Length > 0
            ? Record(0x0018, U16(0x0020), [0], [1], U16(formula.Length), U16(0), U16(1), new byte[4], [0x06], formula)
            : [];
        var title = cyrillic.GetBytes(sheetName);
        byte[] Globals(int sheetAt) =>
        [
            .. Begin(0x0005), .. Record(0x0042, U16(1251)),
            .. Record(0x0085, BitConverter.GetBytes(sheetAt), [0], [0], [(byte)title.Length], title),
            .. name, .. end,
        ];
        var globals = Globals(Globals(0).Length);

        var container = new POIFSFileSystem();
        container.CreateDocument(new MemoryStream([.. globals, .. sheet]), "Book");
        using var file = new MemoryStream();
        container.WriteFileSystem(file);
        return file.ToArray();
    }

    /// <summary>Архив с заданными частями — заготовка офисного файла, какой он есть на диске.</summary>
    public static byte[] Archive(params (string Name, byte[] Content)[] parts)
    {
        using var file = new MemoryStream();
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in parts)
            {
                using var part = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                part.Write(content);
            }
        }
        return file.ToArray();
    }

    /// <summary>Документ Word ровно настолько, чтобы вид определился: дальше его читает конвертер.</summary>
    public static byte[] Document() =>
        Archive(("word/document.xml", Encoding.UTF8.GetBytes("<w:document xmlns:w=\"w\"/>")));

    /// <summary>
    /// PDF с текстовым слоем: по тексту на страницу, пустая строка — страница без текста. Текст
    /// раскладывается по строкам листа: буква, уехавшая за его край, для службы не напечатана.
    /// </summary>
    public static byte[] Pdf(params string[] pages) => Pdf(8, pages);

    /// <param name="points">Кегль текста — для пометки о мелком тексте вписанного вида (issue #1453).</param>
    public static byte[] Pdf(int points, params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            var line = new StringBuilder();
            var y = 800;
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length + word.Length > 90)
                {
                    page.AddText(line.ToString(), points, new PdfPoint(20, y), font);
                    line.Clear();
                    y -= 12;
                    // Лист кончился — длинный текст продолжается на следующем, как у настоящего PDF.
                    if (y < 20) { page = builder.AddPage(PageSize.A4); y = 800; }
                }
                line.Append(word).Append(' ');
            }
            if (line.Length > 0) page.AddText(line.ToString(), points, new PdfPoint(20, y), font);
        }
        return builder.Build();
    }

    /// <summary>Картинка 1×1 — печать или подпись на странице без текста (issue #1457).</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    /// <summary>Файл из репозитория — пара эталонов конвертера лежит в <c>deploy/</c>.</summary>
    public static byte[] RepoFile(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("Не найден корень репозитория выше " + AppContext.BaseDirectory);
        return File.ReadAllBytes(Path.Combine([dir.FullName, .. path]));
    }

    /// <summary>Поток заданной длины из нулей, который считает, сколько из него прочли.</summary>
    public sealed class Counted(long length) : Stream
    {
        public long ReadSoFar { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => ReadSoFar; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = (int)Math.Min(count, length - ReadSoFar);
            Array.Clear(buffer, offset, take);
            ReadSoFar += take;
            return take;
        }
    }

    private sealed class Clients(Converter converter, string? version, Converter? onVersion) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(OfficeConverterOptions.ClientName, name);
            return new HttpClient(new Handler(converter, version, onVersion));
        }
    }

    private sealed class Handler(Converter converter, string? version, Converter? onVersion) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get) return converter(request, ct);
            // Читает у сервиса служба одно — его версию. Другой адрес значит, что она спросила не то.
            Assert.Equal("/version", request.RequestUri!.AbsolutePath);
            if (onVersion is not null) return onVersion(request, ct);
            return Task.FromResult(version is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(version) });
        }
    }
}
