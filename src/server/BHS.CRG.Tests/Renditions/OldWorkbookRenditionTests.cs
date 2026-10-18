using BHS.CRG.Infrastructure.Renditions;
using NPOI.SS.UserModel;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using static BHS.CRG.Tests.Renditions.RenditionFixtures;

namespace BHS.CRG.Tests.Renditions;

/// <summary>
/// Вид для чтения старых книг <c>.xls</c> (issue #1457) — три беды из обзора счетов.
///
/// <list type="bullet">
/// <item>Область печати: у <c>.xls</c> её не отдаёт разборщик ячеек, и текст вне неё — служебные колонки
/// справа от счёта — сверка считала потерянным. Теперь она читается из записей книги, у Excel 97–2003
/// (BIFF8) и у Excel 5.0/95 (BIFF5).</item>
/// <item>«Пустая» вторая страница: у настоящих книг на ней продолжение картинки — печати, подписи,
/// условий оферты, — не поместившейся внизу листа. Пометка говорит, что на странице картинки.</item>
/// <item>Служебный лист, оставленный программой выгрузки, конвертер печатает с новой страницы; пометка
/// называет листы.</item>
/// </list>
/// </summary>
public class OldWorkbookRenditionTests
{
    private const string Lost = "Часть текста не попала на страницу";

    /// <summary>Слова счёта в области печати и служебные — справа от неё.</summary>
    private static readonly (int, int, string)[] Cells = [(0, 0, "alpha bravo"), (1, 0, "charlie delta"), (0, 3, "service marks export")];

    /// <summary>Что конвертер напечатал: область печати, без служебной колонки.</summary>
    private static readonly byte[] AreaOnly = Pdf("alpha bravo charlie delta");

    private static byte[] Biff8(bool withArea) => Workbook(book =>
    {
        var sheet = book.CreateSheet("Sheet");
        foreach (var (row, column, text) in Cells) (sheet.GetRow(row) ?? sheet.CreateRow(row)).CreateCell(column).SetCellValue(text);
        if (withArea) book.SetPrintArea(0, "$A$1:$B$2");
    }, old: true);

    private static byte[] Biff5(bool withArea) => Biff5Workbook(Cells, withArea ? (0, 0, 1, 1) : null);

    public static TheoryData<string> Formats => ["BIFF8", "BIFF5"];

    private static byte[] Book(string format, bool withArea) => format == "BIFF8" ? Biff8(withArea) : Biff5(withArea);

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Текст_вне_области_печати_старой_книги_потерей_не_считается(string format)
    {
        var built = Assert.IsType<Rendition.Built>(await Service(Returns(AreaOnly)).BuildAsync(Book(format, withArea: true)));

        Assert.Empty(built.Notes);
    }

    /// <summary>Контроль: без области та же книга с тем же образом — потеря. Иначе тест выше ничего не значил бы.</summary>
    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Без_области_печати_служебные_колонки_сверяются_как_весь_лист(string format)
    {
        var built = Assert.IsType<Rendition.Built>(await Service(Returns(AreaOnly)).BuildAsync(Book(format, withArea: false)));

        Assert.StartsWith(Lost, Assert.Single(built.Notes));
    }

    /// <summary>Область из двух кусков — объединение ссылок в формуле имени: так её пишет и Excel.</summary>
    [Fact]
    public async Task Область_печати_из_нескольких_кусков_читается_целиком()
    {
        var book = Workbook(b =>
        {
            var row = b.CreateSheet("Sheet").CreateRow(0);
            row.CreateCell(0).SetCellValue("alpha bravo");
            row.CreateCell(1).SetCellValue("service marks export");
            row.CreateCell(2).SetCellValue("charlie delta");
            b.SetPrintArea(0, "$A$1:$A$1,$C$1:$C$1");
        }, old: true);

        var built = Assert.IsType<Rendition.Built>(await Service(Returns(AreaOnly)).BuildAsync(book));

        Assert.Empty(built.Notes);
    }

    /// <summary>Простая ссылка (без листа) и русское имя листа в кодировке книги — область та же.</summary>
    [Fact]
    public async Task Область_старой_книги_простой_ссылкой_и_на_русском_листе_читается()
    {
        var book = Biff5Workbook(Cells, (0, 0, 1, 1), onSheet: false, sheetName: "Счёт");

        var built = Assert.IsType<Rendition.Built>(await Service(Returns(AreaOnly)).BuildAsync(book));

        Assert.Empty(built.Notes);
    }

    /// <summary>
    /// Области на двух листах, перед ними скрытый лист: каждая ложится на свой лист — по имени, а не по
    /// номеру. У <c>.xlsx</c> так ловится и потеря второго имени при чтении оглавления подряд.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Области_нескольких_листов_ложатся_каждая_на_свой_лист(bool old)
    {
        var book = Workbook(b =>
        {
            b.CreateSheet("Hidden").CreateRow(0).CreateCell(0).SetCellValue("secret");
            b.SetSheetHidden(0, SheetState.Hidden);
            string[][] sheets = [["alpha bravo", "service marks"], ["charlie delta", "export noise words"]];
            for (var i = 0; i < sheets.Length; i++)
            {
                var row = b.CreateSheet($"Part{i + 1}").CreateRow(0);
                row.CreateCell(0).SetCellValue(sheets[i][0]);
                row.CreateCell(3).SetCellValue(sheets[i][1]);
                b.SetPrintArea(i + 1, "$A$1:$B$1");
            }
        }, old);

        var built = Assert.IsType<Rendition.Built>(await Service(Returns(AreaOnly)).BuildAsync(book));

        Assert.Equal("Листов с данными: 2 — «Part1», «Part2»; каждый начинается с новой страницы.", Assert.Single(built.Notes));
    }

    // ── Страница без текста ─────────────────────────────────────────────────────────────────────

    /// <summary>Страница с текстом и за ней страницы без текста — с картинкой на листе и без неё.</summary>
    private static byte[] PdfWithBlankPages(params PdfRectangle?[] pictures)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("alpha bravo charlie delta", 10, new PdfPoint(20, 800), font);
        foreach (var picture in pictures)
        {
            var page = builder.AddPage(PageSize.A4);
            if (picture is { } at) page.AddPng(Png, at);
        }
        return builder.Build();
    }

    private static readonly PdfRectangle OnSheet = new(100, 600, 400, 800);

    /// <summary>LibreOffice пишет в страницу и картинку, уехавшую за её край: на листе её не видно.</summary>
    private static readonly PdfRectangle OffSheet = new(100, -400, 400, -100);

    private static async Task<string> BlankNoteOf(params PdfRectangle?[] pictures)
    {
        var book = Biff8(withArea: true);
        var built = Assert.IsType<Rendition.Built>(await Service(Returns(PdfWithBlankPages(pictures))).BuildAsync(book));
        return Assert.Single(built.Notes);
    }

    [Fact]
    public async Task Страница_без_текста_с_картинкой_названа_картинкой()
    {
        Assert.Equal(
            "Страниц: 2, из них без текста: 1 — на ней только картинки (печать, подпись, текст картинкой).",
            await BlankNoteOf(OnSheet));
    }

    /// <summary>
    /// Картинки не видно — страница не названа пустой: печать вставленным рисунком EMF и рамки ячеек
    /// LibreOffice пишет линиями, и «ничего нет» было бы неправдой (ревью #1457).
    /// </summary>
    [Fact]
    public async Task Страница_без_текста_и_без_видимой_картинки_пустой_не_названа()
    {
        Assert.Equal("Страниц: 3, из них без текста: 2.", await BlankNoteOf(OffSheet, null));
    }

    [Fact]
    public async Task Картинки_только_на_части_страниц_без_текста_посчитаны()
    {
        Assert.Equal(
            "Страниц: 4, из них без текста: 3, на 1 из них — только картинки (печать, подпись, текст картинкой).",
            await BlankNoteOf(null, OnSheet, OffSheet));
    }

    // ── Листы ───────────────────────────────────────────────────────────────────────────────────

    private static byte[] Sheets(params (string Name, bool Hidden, string? Text)[] sheets) => Workbook(book =>
    {
        for (var i = 0; i < sheets.Length; i++)
        {
            var (name, hidden, text) = sheets[i];
            var sheet = book.CreateSheet(name);
            if (text is not null) sheet.CreateRow(0).CreateCell(0).SetCellValue(text);
            if (hidden) book.SetSheetHidden(i, SheetState.Hidden);
        }
    });

    [Fact]
    public async Task Листы_с_данными_названы_скрытые_и_пустые_не_в_счёт()
    {
        var book = Sheets(("Invoice", false, "alpha bravo"), ("Hidden", true, "secret words"), ("Empty", false, null),
            ("Params", false, "charlie delta"));

        var built = Assert.IsType<Rendition.Built>(await Service(Returns(AreaOnly)).BuildAsync(book));

        Assert.Equal("Листов с данными: 2 — «Invoice», «Params»; каждый начинается с новой страницы.", Assert.Single(built.Notes));
    }

    [Fact]
    public async Task Один_лист_с_данными_не_помечается()
    {
        var book = Sheets(("Invoice", false, "alpha bravo charlie delta"), ("Empty", false, null));

        var built = Assert.IsType<Rendition.Built>(await Service(Returns(AreaOnly)).BuildAsync(book));

        Assert.Empty(built.Notes);
    }

    [Fact]
    public async Task Длинный_список_листов_назван_числом()
    {
        var names = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf" };
        var book = Sheets([.. names.Select(name => (name, false, (string?)name))]);
        var pdf = Pdf(string.Join(" ", names));

        var built = Assert.IsType<Rendition.Built>(await Service(Returns(pdf)).BuildAsync(book));

        Assert.Equal("Листов с данными: 7 — «alpha», «bravo», «charlie», «delta», «echo» и ещё 2; каждый начинается с новой страницы.",
            Assert.Single(built.Notes));
    }

    // ── Эталон ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Пара из <c>deploy/</c>: выдуманная старая книга со всеми тремя случаями и PDF, который из неё сделал
    /// НАСТОЯЩИЙ конвертер. Картинка внизу листа ушла на вторую страницу, служебная колонка вне области
    /// печати на страницу не попала, лист «Параметры» — третья страница. Что живой конвертер отдаёт тот же
    /// текст, сверяет <c>deploy/converter.tests.sh</c>.
    /// </summary>
    [Fact]
    public async Task Эталонная_старая_книга_названа_честно_и_проходит_сверку()
    {
        var service = Service(Returns(RepoFile("deploy", "converter-old.pdf")));

        var built = Assert.IsType<Rendition.Built>(await service.BuildAsync(RepoFile("deploy", "converter-old.xls")));

        Assert.Equal(3, built.Pages);
        Assert.Equal(
        [
            "Страниц: 3, из них без текста: 1 — на ней только картинки (печать, подпись, текст картинкой).",
            "Листов с данными: 2 — «Счёт», «Параметры»; каждый начинается с новой страницы.",
        ], built.Notes);
    }
}
