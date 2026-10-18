using System.Net;
using System.Text;
using BHS.CRG.Api.Renditions;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Renditions;
using BHS.CRG.Modules.Files;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using static BHS.CRG.Tests.Renditions.RenditionFixtures;

namespace BHS.CRG.Tests.Renditions;

/// <summary>
/// Читаемый образ файла (issue #1268): три ответа службы и отказы, которые обязаны прийти ДО
/// конвертера.
///
/// <para>Конвертер вид файла не проверяет: текст с расширением <c>.xlsx</c> он возвращает с кодом
/// 200 и PDF, в котором этот текст напечатан. Поэтому всё, что можно сказать о файле, не открывая
/// его LibreOffice, говорится раньше — и в этих тестах подставной конвертер валит тест, если до
/// него дошли.</para>
/// </summary>
public class RenditionServiceTests
{
    private static readonly string[] Invoice =
        ["Invoice number seventeen", "Supplier Etalon Snab", "Buyer Montazh Proba", "Cable power three cores", "Total amount due"];

    private static readonly string InvoiceText = string.Join(" ", Invoice);

    [Fact]
    public async Task Pdf_и_изображение_читаются_как_есть_и_к_конвертеру_не_идут()
    {
        var service = Service(MustNotBeCalled);

        Assert.Equal(new Rendition.AsIs(FileKinds.Pdf), await service.BuildAsync(Pdf("anything")));
        Assert.Equal(new Rendition.AsIs(FileKinds.Png),
            await service.BuildAsync([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]));
    }

    [Fact]
    public async Task Книга_превращается_в_образ_а_конвертеру_уходит_имя_по_виду_файла()
    {
        string? sentAs = null;
        Uri? asked = null;
        var service = Service((request, _) =>
        {
            asked = request.RequestUri;
            sentAs = SentName(request);
            return Task.FromResult(PdfReply(Pdf(InvoiceText)));
        });

        var built = Assert.IsType<Rendition.Built>(await service.BuildAsync(Workbook(Invoice)));

        Assert.Equal(1, built.Pages);
        Assert.Empty(built.Notes);
        Assert.Equal("http://converter:3000/forms/libreoffice/convert", asked?.ToString());
        Assert.Equal("file.xlsx", sentAs);
    }

    /// <summary>
    /// Пара эталонов из <c>deploy/</c>: синтетический счёт с объединёнными ячейками, числами и
    /// датами и PDF, который из него сделал НАСТОЯЩИЙ конвертер. Что живой конвертер по-прежнему
    /// отдаёт этот же текст, сверяет <c>deploy/converter.tests.sh</c>.
    /// </summary>
    [Fact]
    public async Task Эталонный_счёт_и_его_настоящий_образ_проходят_постусловия_без_пометок()
    {
        var service = Service(Returns(RepoFile("deploy", "converter-reference.pdf")));

        var built = Assert.IsType<Rendition.Built>(
            await service.BuildAsync(RepoFile("deploy", "converter-reference.xlsx")));

        Assert.Equal(1, built.Pages);
        Assert.Empty(built.Notes);
    }

    [Fact]
    public async Task Старый_Excel_тоже_книга()
    {
        string? sentAs = null;
        var service = Service((request, _) =>
        {
            sentAs = SentName(request);
            return Task.FromResult(PdfReply(Pdf(InvoiceText)));
        });
        var old = Workbook(book => book.CreateSheet("Sheet").CreateRow(0).CreateCell(0).SetCellValue(InvoiceText), old: true);

        Assert.IsType<Rendition.Built>(await service.BuildAsync(old));
        Assert.Equal("file.xls", sentAs);
    }

    /// <summary>Скрытое не печатается — и потерей не считается: иначе книга со служебным листом получала бы отказ.</summary>
    [Fact]
    public async Task Скрытые_листы_строки_и_колонки_с_образом_не_сверяются()
    {
        var book = Workbook(workbook =>
        {
            var shown = workbook.CreateSheet("Shown");
            shown.CreateRow(0).CreateCell(0).SetCellValue(InvoiceText);
            shown.AddMergedRegion(new CellRangeAddress(0, 0, 0, 3));
            shown.CreateRow(1).CreateCell(0).SetCellValue(3834.16);
            shown.CreateRow(2).CreateCell(0).SetCellValue("hiddenrow secret words never printed");
            shown.GetRow(2).ZeroHeight = true;
            shown.GetRow(0).CreateCell(5).SetCellValue("hiddencolumn secret words never printed");
            shown.SetColumnHidden(5, true);
            workbook.CreateSheet("Service").CreateRow(0).CreateCell(0).SetCellValue("hiddensheet settings template rows");
            workbook.SetSheetHidden(1, SheetState.Hidden);
        });

        var built = Assert.IsType<Rendition.Built>(await Service(Returns(Pdf(InvoiceText))).BuildAsync(book));

        Assert.Empty(built.Notes);
    }

    // ── Отказы до конвертера ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Вид_который_не_читается_вовсе_получает_отказ_с_перечнем_читаемых()
    {
        var gif = Encoding.ASCII.GetBytes("GIF89a").Concat(new byte[32]).ToArray();

        var refused = Assert.IsType<Rendition.Refused>(await Service(MustNotBeCalled).BuildAsync(gif));

        Assert.Equal(RenditionRefusal.WrongFormat, refused.Kind);
        Assert.Equal("Файл такого вида не читается. Читаются PDF, PNG, JPEG, Excel и Word.", refused.Reason);
        Assert.False(refused.RetryHelps);
    }

    /// <summary>
    /// Тот самый случай: не офисный файл с офисным расширением. Имя сюда не приходит вовсе — вид
    /// даёт содержимое, и текст остаётся текстом, как бы он ни назывался.
    /// </summary>
    [Fact]
    public async Task Текст_назвавшийся_таблицей_к_конвертеру_не_попадает()
    {
        var text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hello, this is not an office file\n", 100)));

        var refused = Assert.IsType<Rendition.Refused>(await Service(MustNotBeCalled).BuildAsync(text));

        Assert.Equal(RenditionRefusal.WrongFormat, refused.Kind);
    }

    [Fact]
    public async Task Архив_без_офисных_частей_файл_другого_вида_а_не_повреждённый()
    {
        var zip = Archive(("readme.txt", Encoding.UTF8.GetBytes("just an archive")));

        var refused = Assert.IsType<Rendition.Refused>(await Service(MustNotBeCalled).BuildAsync(zip));

        Assert.Equal(RenditionRefusal.WrongFormat, refused.Kind);
    }

    [Fact]
    public async Task Обрезанная_книга_повреждена()
    {
        var whole = Workbook(Invoice);

        var refused = Assert.IsType<Rendition.Refused>(await Service(MustNotBeCalled).BuildAsync(whole[..(whole.Length / 2)]));

        Assert.Equal(RenditionRefusal.Corrupted, refused.Kind);
        Assert.StartsWith("Файл повреждён", refused.Reason);
    }

    /// <summary>
    /// Наш разборщик книгу не прочёл — это ещё не «повреждена»: он помощник сверки, а не ворота
    /// перед конвертером, и LibreOffice понимает больше (ревью PR #1280). Что с книгой на самом
    /// деле, говорит конвертер; а образ, построенный без сверки, обязан об этом сказать.
    /// </summary>
    [Fact]
    public async Task Книга_которую_не_прочёл_наш_разборщик_идёт_к_конвертеру_без_сверки()
    {
        var odd = Archive(("xl/workbook.xml", Encoding.UTF8.GetBytes("<not a workbook")));

        var refused = Assert.IsType<Rendition.Refused>(
            await Service(Answers(HttpStatusCode.InternalServerError)).BuildAsync(odd));
        var built = Assert.IsType<Rendition.Built>(await Service(Returns(Pdf(InvoiceText))).BuildAsync(odd));

        Assert.Equal(RenditionRefusal.Failed, refused.Kind);
        // Оглавление не читается и у раскладки (issue #1453): книга ушла как есть, и об этом сказано.
        Assert.Equal([ExcelPageLayout.FailedNote, OfficeRenditionBuilder.UncheckedNote], built.Notes);
        Assert.Equal(RenditionLayout.Skipped, built.Layout);
    }

    // ── Раскладка по профилю печати (issue #1453) ───────────────────────────────────────────────

    /// <summary>
    /// Книга без своей раскладки уходит к конвертеру вписанной КОПИЕЙ, а сверка идёт по словам
    /// ОРИГИНАЛА: вписанная книга — первый случай, когда к конвертеру идёт не тот массив, по которому
    /// считались слова.
    /// </summary>
    [Fact]
    public async Task Книга_без_раскладки_уходит_вписанной_копией_а_оригинал_цел()
    {
        var book = Workbook(b => b.CreateSheet("Sheet").CreateRow(0).CreateCell(0).SetCellValue(InvoiceText), ownLayout: false);
        var before = book.ToArray();
        byte[]? sent = null;
        var service = Service(async (request, ct) =>
        {
            sent = await Assert.Single(Assert.IsType<MultipartFormDataContent>(request.Content)).ReadAsByteArrayAsync(ct);
            return PdfReply(Pdf(InvoiceText));
        });

        var built = Assert.IsType<Rendition.Built>(await service.BuildAsync(book));

        Assert.Equal(before, book);
        Assert.NotEqual(book, sent);
        Assert.Contains("fitToPage=\"1\"", SheetText(sent!));
        Assert.Equal(RenditionLayout.Applied, built.Layout);
        Assert.Contains("\"fit\":\"width\"", built.LayoutApplied);
        // Слов оригинала в образе хватает — пометки о потере нет, есть только о вписывании.
        Assert.Equal([ExcelPageLayout.AppliedNote(PageProfile.Default, 1)], built.Notes);
    }

    /// <summary>
    /// Вписанная копия получила отказ «не вышло» — образ строится второй раз, по ОРИГИНАЛУ: виновата
    /// могла быть правка, а запомненный отказ закрыл бы вид, который по файлу строится. Исход в записи —
    /// <see cref="RenditionLayout.Skipped" /> с пометкой, а не «вписан».
    /// </summary>
    [Fact]
    public async Task Отказ_на_вписанной_копии_повторяется_по_оригиналу()
    {
        var book = Workbook(b => b.CreateSheet("Sheet").CreateRow(0).CreateCell(0).SetCellValue(InvoiceText), ownLayout: false);
        var sent = new List<byte[]>();
        var service = Service(async (request, ct) =>
        {
            sent.Add(await Assert.Single(Assert.IsType<MultipartFormDataContent>(request.Content)).ReadAsByteArrayAsync(ct));
            // Первый ответ — PDF без слов книги: постусловия отвечают Failed.
            return PdfReply(Pdf(sent.Count == 1 ? "Something else entirely" : InvoiceText));
        });

        var built = Assert.IsType<Rendition.Built>(await service.BuildAsync(book));

        Assert.Equal(2, sent.Count);
        Assert.NotEqual(book, sent[0]);
        Assert.Equal(book, sent[1]);
        Assert.Equal((RenditionLayout.Skipped, (string?)null), (built.Layout, built.LayoutApplied));
        Assert.Equal([ExcelPageLayout.FailedNote], built.Notes);
    }

    /// <summary>
    /// Повтор — только у вписанной копии и только на «не вышло». Книга автора с тем же отказом
    /// повторно не строится: по тому же массиву вышло бы то же. Недоступный конвертер — не вина
    /// правки, и второй вызов был бы вторым ожиданием.
    /// </summary>
    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.BadGateway)]
    public async Task Прочие_отказы_не_повторяются(bool ownLayout, HttpStatusCode status)
    {
        var book = Workbook(b => b.CreateSheet("Sheet").CreateRow(0).CreateCell(0).SetCellValue(InvoiceText), ownLayout: ownLayout);
        var calls = 0;
        var service = Service((_, _) =>
        {
            calls++;
            return Task.FromResult(status == HttpStatusCode.OK
                ? PdfReply(Pdf("Something else entirely"))
                : new HttpResponseMessage(status));
        });

        Assert.IsType<Rendition.Refused>(await service.BuildAsync(book));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Книга_со_своей_раскладкой_уходит_тем_же_массивом()
    {
        var book = Workbook(Invoice);
        byte[]? sent = null;
        var service = Service(async (request, ct) =>
        {
            sent = await Assert.Single(Assert.IsType<MultipartFormDataContent>(request.Content)).ReadAsByteArrayAsync(ct);
            return PdfReply(Pdf(InvoiceText));
        });

        var built = Assert.IsType<Rendition.Built>(await service.BuildAsync(book));

        Assert.Equal(book, sent);
        Assert.Equal(RenditionLayout.BookOwn, built.Layout);
        Assert.Null(built.LayoutApplied);
    }

    [Fact]
    public async Task Word_и_старый_Excel_профилем_не_раскладываются()
    {
        var old = Workbook(b => b.CreateSheet("Sheet").CreateRow(0).CreateCell(0).SetCellValue(InvoiceText), old: true);

        var xls = Assert.IsType<Rendition.Built>(await Service(Returns(Pdf(InvoiceText))).BuildAsync(old));
        var docx = Assert.IsType<Rendition.Built>(await Service(Returns(Pdf("Word document text"))).BuildAsync(Document()));

        Assert.Equal((RenditionLayout.NotBook, RenditionLayout.NotBook), (xls.Layout, docx.Layout));
    }

    /// <summary>
    /// Мелкий текст — пометка только у вписанной книги: уменьшили её мы. Кегль автора — его решение.
    /// </summary>
    [Theory]
    [InlineData(false, 3, true)]
    [InlineData(false, 9, false)]
    [InlineData(true, 3, false)]
    public async Task Вписанный_вид_с_мелким_текстом_несёт_пометку(bool ownLayout, int points, bool noted)
    {
        var book = Workbook(b => b.CreateSheet("Sheet").CreateRow(0).CreateCell(0).SetCellValue(InvoiceText), ownLayout: ownLayout);

        var built = Assert.IsType<Rendition.Built>(await Service(Returns(Pdf(points, InvoiceText))).BuildAsync(book));

        Assert.Equal(noted, built.Notes.Contains(OfficeRenditionBuilder.SmallTextNote));
    }

    /// <summary>
    /// Профиль «не вписывать» (issue #1454) кегль не уменьшает — и о мелком тексте молчит: мелкий он у
    /// автора, а это его решение.
    /// </summary>
    [Fact]
    public async Task Профиль_без_вписывания_о_мелком_тексте_не_говорит()
    {
        var book = Workbook(b => b.CreateSheet("Sheet").CreateRow(0).CreateCell(0).SetCellValue(InvoiceText), ownLayout: false);

        var built = Assert.IsType<Rendition.Built>(await Service(Returns(Pdf(3, InvoiceText)))
            .BuildAsync(new MemoryStream(book), PageProfile.Default with { FitWidth = false }, CancellationToken.None));

        Assert.Equal(RenditionLayout.Applied, built.Layout);
        Assert.DoesNotContain(OfficeRenditionBuilder.SmallTextNote, built.Notes);
    }

    /// <summary>
    /// Пара из <c>deploy/</c>: выдуманная широкая книга без раскладки и её копия, которую вписывает этот
    /// код. Пара обязана оставаться парой: живая проверка (<c>deploy/converter.tests.sh</c>) отправляет
    /// конвертеру именно копию и ждёт одну страницу A4 — разойдись копия с тем, что делает код, и
    /// живая проверка проверяла бы не то.
    /// </summary>
    [Fact]
    public void Вписанная_копия_из_deploy_это_то_что_делает_раскладка()
    {
        var laid = ExcelPageLayout.Apply(RepoFile("deploy", "converter-wide.xlsx"), PageProfile.Default, CancellationToken.None);

        Assert.Equal(RenditionLayout.Applied, laid.Outcome);
        Assert.Equal(Parts(RepoFile("deploy", "converter-wide-laid.xlsx")), Parts(laid.File));
    }

    /// <summary>
    /// Настоящий образ вписанной книги (PDF настоящего конвертера) проходит сверку по словам ОРИГИНАЛА, а
    /// его кегль — уменьшенный: 10 pt книги стали 7,1 (проба #1453 — та же цифра у PyMuPDF).
    /// </summary>
    [Fact]
    public async Task Настоящий_образ_вписанной_книги_сверяется_с_оригиналом_и_кегль_уменьшен()
    {
        var pdf = RepoFile("deploy", "converter-wide-laid.pdf");

        var built = Assert.IsType<Rendition.Built>(await Service(Returns(pdf)).BuildAsync(RepoFile("deploy", "converter-wide.xlsx")));

        Assert.Equal((1, RenditionLayout.Applied), (built.Pages, built.Layout));
        Assert.DoesNotContain(built.Notes, note => note.StartsWith("Часть текста", StringComparison.Ordinal));
        Assert.InRange(built.MedianPointSize!.Value, 6.5, 7.5);
    }

    private static Dictionary<string, string> Parts(byte[] book)
    {
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(book));
        return zip.Entries.ToDictionary(e => e.FullName, e => new StreamReader(e.Open()).ReadToEnd());
    }

    private static string SheetText(byte[] book)
    {
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(book));
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Две ячейки по углам листа: файл в несколько килобайт, а между ячейками — миллион строк на
    /// шестнадцать тысяч колонок, и разборщик отдаёт их все. Счётчик заполненных ячеек тут равен
    /// двум — считать надо пройденные.
    /// </summary>
    [Fact]
    public async Task Книга_с_ячейками_по_углам_листа_слишком_велика_и_отказ_приходит_быстро()
    {
        var corners = Workbook(book =>
        {
            var sheet = book.CreateSheet("Sheet");
            sheet.CreateRow(0).CreateCell(0).SetCellValue("first");
            sheet.CreateRow(1_048_575).CreateCell(16_383).SetCellValue("last");
        });
        Assert.True(corners.Length < 100 * 1024);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var refused = Assert.IsType<Rendition.Refused>(await Service(MustNotBeCalled).BuildAsync(corners));

        Assert.Equal(RenditionRefusal.TooLarge, refused.Kind);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"отказ шёл {clock.Elapsed}");
    }

    /// <summary>
    /// Счёт в области печати, рядом на листе — справочные колонки. На страницу идёт только область,
    /// и остальное потерей не считается: иначе здоровая книга получала бы отказ навсегда.
    /// </summary>
    [Fact]
    public async Task Текст_вне_области_печати_с_образом_не_сверяется()
    {
        var book = Workbook(workbook =>
        {
            var sheet = workbook.CreateSheet("Invoice sheet");
            sheet.CreateRow(0).CreateCell(0).SetCellValue(InvoiceText);
            for (var row = 0; row < 40; row++)
                (sheet.GetRow(row) ?? sheet.CreateRow(row)).CreateCell(8).SetCellValue($"reference{row} lookup{row} never{row} printed{row}");
            workbook.SetPrintArea(0, 0, 3, 0, 5);
        });

        var built = Assert.IsType<Rendition.Built>(await Service(Returns(Pdf(InvoiceText))).BuildAsync(book));

        Assert.Empty(built.Notes);
    }

    /// <summary>Файл чужого вида целиком не читается: чтобы назвать его чужим, хватает начала.</summary>
    [Fact]
    public async Task Файл_чужого_вида_целиком_в_память_не_берётся()
    {
        var gif = new ReadCounting([.. Encoding.ASCII.GetBytes("GIF89a"), .. new byte[5 * 1024 * 1024]]);

        var refused = Assert.IsType<Rendition.Refused>(await Service(MustNotBeCalled).BuildAsync(gif, PageProfile.Default, CancellationToken.None));

        Assert.Equal(RenditionRefusal.WrongFormat, refused.Kind);
        Assert.True(gif.ReadSoFar < 64 * 1024, $"прочитано {gif.ReadSoFar} байт");
    }

    private sealed class ReadCounting(byte[] content) : MemoryStream(content)
    {
        public long ReadSoFar { get; private set; }

        public override int Read(Span<byte> buffer)
        {
            var read = base.Read(buffer);
            ReadSoFar += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            ReadSoFar += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await base.ReadAsync(buffer, ct);
            ReadSoFar += read;
            return read;
        }
    }

    /// <summary>
    /// Файл под паролем — уже не архив, а контейнер с зашифрованным потоком: вид у него не
    /// определяется, и без этой проверки человек прочёл бы «файл такого вида не читается» про
    /// собственный Excel.
    /// </summary>
    [Fact]
    public async Task Зашифрованная_книга_защищена_паролем()
    {
        // Настоящий файл под паролем: тот же эталонный счёт, зашифрованный средствами Office.
        var encrypted = RepoFile("deploy", "converter-protected.xlsx");

        var refused = Assert.IsType<Rendition.Refused>(await Service(MustNotBeCalled).BuildAsync(encrypted));

        Assert.Equal(RenditionRefusal.Protected, refused.Kind);
        Assert.Contains("защищён паролем", refused.Reason);
        // Что под паролем, не видно — так же зашифрована и презентация. Совет снять пароль идёт с
        // оговоркой, что именно будет прочитано: иначе он вёл бы ко второму отказу.
        Assert.Contains("если это Excel или Word", refused.Reason);
    }

    /// <summary>
    /// Старый Excel под паролем остаётся книгой — вид определяется, а открыть её нельзя. Файл
    /// настоящий: эталонный счёт, сохранённый LibreOffice под паролем.
    ///
    /// <para>⚠️ Узнать о пароле обязаны МЫ, до конвертера: на такой файл он отвечает не «нужен
    /// пароль», как на новый формат, а общим «не удалось преобразовать» — и человек прочёл бы
    /// совет повторить.</para>
    /// </summary>
    [Fact]
    public async Task Старый_Excel_под_паролем_защищён()
    {
        var locked = RepoFile("deploy", "converter-protected.xls");

        var refused = Assert.IsType<Rendition.Refused>(await Service(MustNotBeCalled).BuildAsync(locked));

        Assert.Equal(RenditionRefusal.Protected, refused.Kind);
    }

    [Fact]
    public async Task Пустая_книга_пуста()
    {
        var empty = Workbook(book => book.CreateSheet("Sheet"));

        var refused = Assert.IsType<Rendition.Refused>(await Service(MustNotBeCalled).BuildAsync(empty));

        Assert.Equal(RenditionRefusal.Empty, refused.Kind);
    }

    /// <summary>
    /// Архивная бомба: тридцать мегабайт нулей в файле на тридцать килобайт. Размер файла о ней не
    /// говорит ничего — записи читаются на самом деле, и чтение обрывается на пределе.
    /// </summary>
    [Fact]
    public async Task Архив_раздувающийся_сверх_предела_слишком_велик()
    {
        var bomb = Archive(
            ("xl/workbook.xml", Encoding.UTF8.GetBytes("<workbook/>")),
            ("xl/worksheets/sheet1.xml", new byte[30 * 1024 * 1024]));
        Assert.True(bomb.Length < 100 * 1024, $"заготовка не сжалась: {bomb.Length} байт");

        var refused = Assert.IsType<Rendition.Refused>(await Service(MustNotBeCalled).BuildAsync(bomb));

        Assert.Equal(RenditionRefusal.TooLarge, refused.Kind);
        Assert.Contains("в распакованном виде", refused.Reason);
    }

    [Fact]
    public async Task Архив_из_тысяч_частей_слишком_велик()
    {
        var parts = Enumerable.Range(0, RenditionLimits.ArchiveMaxEntries + 1)
            .Select(index => ($"word/part{index}.xml", Array.Empty<byte>()))
            .Prepend(("word/document.xml", Encoding.UTF8.GetBytes("<w:document/>")))
            .ToArray();

        var refused = Assert.IsType<Rendition.Refused>(await Service(MustNotBeCalled).BuildAsync(Archive(parts)));

        Assert.Equal(RenditionRefusal.TooLarge, refused.Kind);
    }

    /// <summary>Предел офисного файла ниже общего предела вложения — иначе он не предел.</summary>
    [Fact]
    public void Предел_офисного_файла_ниже_предела_вложения() =>
        Assert.True(RenditionLimits.OfficeMaxBytes < FileKindCatalog.MaxBytes);

    /// <summary>
    /// Каждый вид «через образ» из реестра служба обязана уметь отдать строителю: вид, вписанный в
    /// реестр и забытый здесь, иначе обнаружился бы исключением у пользователя.
    /// </summary>
    [Fact]
    public void У_каждого_вида_через_образ_есть_формат_строителя()
    {
        var formats = FileKindCatalog.All.Where(kind => kind.Reading == FileReading.Rendition)
            .Select(kind => RenditionService.FormatOf(kind.Mime)).ToList();

        Assert.NotEmpty(formats);
        Assert.Equal(formats.Count, formats.Distinct().Count());
    }
}
