using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BHS.CRG.Api.Renditions;
using BHS.CRG.Infrastructure.OfficeConversion;
using BHS.CRG.Infrastructure.Renditions;
using Microsoft.Extensions.Logging.Abstractions;
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
    public static OfficeConverterClient Client(
        Converter converter, string? baseUrl = "http://converter:3000", TimeSpan? timeout = null,
        TimeSpan? gateWait = null, string? version = null)
        => new(new Clients(converter, version), new OfficeConverterOptions { BaseUrl = baseUrl },
            NullLogger<OfficeConverterClient>.Instance)
        {
            Timeout = timeout ?? OfficeConverterOptions.ClientTimeout,
            GateWait = gateWait ?? RenditionLimits.GateWait,
        };

    public static RenditionService Service(Converter converter, string? version = null) =>
        new(new OfficeRenditionBuilder(Client(converter, version: version), NullLogger<OfficeRenditionBuilder>.Instance));

    public static Task<Rendition> BuildAsync(this RenditionService service, byte[] file) =>
        service.BuildAsync(new MemoryStream(file), CancellationToken.None);

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

    public static byte[] Workbook(Action<IWorkbook> fill, bool old = false)
    {
        using IWorkbook book = old ? new NPOI.HSSF.UserModel.HSSFWorkbook() : new XSSFWorkbook();
        fill(book);
        using var file = new MemoryStream();
        book.Write(file, leaveOpen: true);
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
    public static byte[] Pdf(params string[] pages)
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
                    page.AddText(line.ToString(), 8, new PdfPoint(20, y), font);
                    line.Clear();
                    y -= 12;
                    // Лист кончился — длинный текст продолжается на следующем, как у настоящего PDF.
                    if (y < 20) { page = builder.AddPage(PageSize.A4); y = 800; }
                }
                line.Append(word).Append(' ');
            }
            if (line.Length > 0) page.AddText(line.ToString(), 8, new PdfPoint(20, y), font);
        }
        return builder.Build();
    }

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

    private sealed class Clients(Converter converter, string? version) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(OfficeConverterOptions.ClientName, name);
            return new HttpClient(new Handler(converter, version));
        }
    }

    private sealed class Handler(Converter converter, string? version) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Get) return converter(request, ct);
            // Читает у сервиса служба одно — его версию. Другой адрес значит, что она спросила не то.
            Assert.Equal("/version", request.RequestUri!.AbsolutePath);
            return Task.FromResult(version is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(version) });
        }
    }
}
