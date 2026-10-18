using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Renditions;

namespace BHS.CRG.Tests.Renditions;

/// <summary>
/// Раскладка книги по профилю печати (issue #1453): правило «автор задал раскладку сам» и правка копии.
///
/// <para>Книги собраны вручную, частями архива: проверяется ровно та разметка листа, которую пишет
/// Excel или 1С, — генератор таблиц дописал бы свою. Данные выдуманные.</para>
/// </summary>
public class ExcelPageLayoutTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace X = Main;
    private const string SheetPath = "xl/worksheets/sheet1.xml";

    private const string Data =
        "<sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t xml:space=\"preserve\">Счёт на оплату № 7 </t></is></c>" +
        "<c r=\"B1\"><v>3834.16</v></c></row></sheetData>";

    // ── Правило ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Лист_без_своей_раскладки_вписывается_по_ширине_на_A4_с_полями_профиля()
    {
        var laid = Apply(Book(Sheet(Data + Margins())));

        Assert.Equal(RenditionLayout.Applied, laid.Outcome);
        var sheet = SheetOf(laid.File);
        Assert.Equal("1", sheet.Element(X + "sheetPr")!.Element(X + "pageSetUpPr")!.Attribute("fitToPage")!.Value);
        var setup = sheet.Element(X + "pageSetup")!;
        Assert.Equal(("9", "portrait", "1", "0"), (Attr(setup, "paperSize"), Attr(setup, "orientation"),
            Attr(setup, "fitToWidth"), Attr(setup, "fitToHeight")));
        var margins = sheet.Element(X + "pageMargins")!;
        // 5 мм в дюймах; колонтитула нет — его поле ноль.
        Assert.All(new[] { "left", "right", "top", "bottom" }, side => Assert.Equal("0.19685", Attr(margins, side)));
        Assert.Equal(("0", "0"), (Attr(margins, "header"), Attr(margins, "footer")));
        Assert.Equal([ExcelPageLayout.AppliedNote(PageProfile.Default, 1)], laid.Notes);
        Assert.Contains("\"profile\":\"default\"", laid.Applied);
    }

    /// <summary>Каждый признак своей раскладки по отдельности — книга уходит к конвертеру ТЕМ ЖЕ массивом.</summary>
    [Theory]
    [InlineData("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>", "")]
    [InlineData("<sheetPr><pageSetUpPr fitToPage=\"true\"/></sheetPr>", "")]
    [InlineData("", "<pageSetup paperSize=\"9\" scale=\"60\"/>")]
    [InlineData("", "<colBreaks count=\"1\" manualBreakCount=\"1\"><brk id=\"5\" max=\"1048575\" man=\"1\"/></colBreaks>")]
    public void Своя_раскладка_автора_уважается_и_книга_не_переписывается(string head, string tail)
    {
        var book = Book(Sheet(head, Data + Margins() + tail));

        var laid = Apply(book);

        Assert.Equal(RenditionLayout.BookOwn, laid.Outcome);
        Assert.Same(book, laid.File);
        Assert.Empty(laid.Notes);
        Assert.Null(laid.Applied);
    }

    /// <summary>
    /// <c>fitToWidth</c> без флага <c>fitToPage</c> — не своя раскладка: LibreOffice его не исполняет
    /// (проба #1453 (а) — две страницы и кегль 10, как без раскладки вовсе). Бумага и ориентация автора
    /// при этом остаются его.
    /// </summary>
    [Fact]
    public void Вписывание_без_флага_не_считается_своим_а_бумага_и_ориентация_автора_остаются()
    {
        var laid = Apply(Book(Sheet(Data + Margins() +
            "<pageSetup paperSize=\"8\" orientation=\"landscape\" fitToWidth=\"2\" fitToHeight=\"3\" horizontalDpi=\"600\"/>")));

        Assert.Equal(RenditionLayout.Applied, laid.Outcome);
        var setup = SheetOf(laid.File).Element(X + "pageSetup")!;
        Assert.Equal(("8", "landscape", "1", "0", "600"), (Attr(setup, "paperSize"), Attr(setup, "orientation"),
            Attr(setup, "fitToWidth"), Attr(setup, "fitToHeight"), Attr(setup, "horizontalDpi")));
    }

    [Theory]
    [InlineData("<pageSetup paperSize=\"9\" scale=\"100\"/>")]
    [InlineData("<colBreaks count=\"0\"/>")]
    public void Масштаб_100_и_пустой_перечень_разрывов_раскладкой_не_считаются(string tail)
        => Assert.Equal(RenditionLayout.Applied, Apply(Book(Sheet(Data + Margins() + tail))).Outcome);

    /// <summary>Колонтитул с текстом остаётся на своём поле: под нулевым он лёг бы на таблицу.</summary>
    [Fact]
    public void Колонтитул_с_текстом_сохраняет_своё_поле()
    {
        var laid = Apply(Book(Sheet(Data + Margins(header: "0.4", footer: "0.35") +
            "<headerFooter><oddFooter>&amp;P из &amp;N</oddFooter></headerFooter>")));

        var margins = SheetOf(laid.File).Element(X + "pageMargins")!;
        Assert.Equal(("0.4", "0.35"), (Attr(margins, "header"), Attr(margins, "footer")));
    }

    [Fact]
    public void Книга_Strict_уходит_как_есть_с_пометкой()
    {
        var book = Book(Sheet(Data), workbookNs: "http://purl.oclc.org/ooxml/spreadsheetml/main");

        var laid = Apply(book);

        Assert.Equal(RenditionLayout.Skipped, laid.Outcome);
        Assert.Same(book, laid.File);
        Assert.Equal([ExcelPageLayout.FailedNote], laid.Notes);
    }

    [Fact]
    public void Лист_который_не_разбирается_не_делает_отказа_книга_уходит_как_есть()
    {
        var book = Book("<worksheet xmlns=\"" + Main + "\"><sheetData><row></worksheet>");

        var laid = Apply(book);

        Assert.Equal(RenditionLayout.Skipped, laid.Outcome);
        Assert.Same(book, laid.File);
        Assert.Equal([ExcelPageLayout.FailedNote], laid.Notes);
    }

    // ── Правка ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Недостающее встаёт на место схемы <c>CT_Worksheet</c>: после <c>printOptions</c>, перед
    /// колонтитулом и рисунком. Неверный порядок LibreOffice 8.37 прощает, Excel — чинит книгу.
    /// </summary>
    [Fact]
    public void Недостающие_элементы_встают_в_порядке_схемы()
    {
        var laid = Apply(Book(Sheet("<dimension ref=\"A1:B1\"/>",
            Data + "<mergeCells count=\"1\"><mergeCell ref=\"A1:B1\"/></mergeCells><printOptions gridLines=\"1\"/>" +
            "<headerFooter/><drawing xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rId1\"/>" +
            "<extLst/>")));

        var names = SheetOf(laid.File).Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(["sheetPr", "dimension", "sheetData", "mergeCells", "printOptions", "pageMargins", "pageSetup",
            "headerFooter", "drawing", "extLst"], names);
    }

    [Fact]
    public void Свойства_листа_и_атрибуты_автора_сохраняются()
    {
        var laid = Apply(Book(Sheet("<sheetPr codeName=\"Лист1\"><tabColor rgb=\"FF00FF00\"/><pageSetUpPr autoPageBreaks=\"0\"/></sheetPr>",
            Data + Margins() + "<pageSetup paperSize=\"9\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:id=\"rId7\"/>")));

        var sheet = SheetOf(laid.File);
        var props = sheet.Element(X + "sheetPr")!;
        Assert.Equal("Лист1", Attr(props, "codeName"));
        Assert.Equal(["tabColor", "pageSetUpPr"], props.Elements().Select(e => e.Name.LocalName));
        var fit = props.Element(X + "pageSetUpPr")!;
        Assert.Equal(("0", "1"), (Attr(fit, "autoPageBreaks"), Attr(fit, "fitToPage")));
        Assert.Equal("rId7", sheet.Element(X + "pageSetup")!
            .Attribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"))!.Value);
    }

    /// <summary>Правка трогает только разметку страницы: ячейки и прочие части архива — те же.</summary>
    [Fact]
    public void Ячейки_и_прочие_части_архива_остаются_прежними()
    {
        var book = Book(Sheet(Data + Margins()), extra: ("xl/sharedStrings.xml", "<sst xmlns=\"" + Main + "\" count=\"0\"/>"));

        var laid = Apply(book);

        var before = Parts(book);
        var after = Parts(laid.File);
        Assert.Equal(before.Keys, after.Keys);
        foreach (var (name, content) in before.Where(p => p.Key != SheetPath))
            Assert.Equal(content, after[name]);
        Assert.True(XNode.DeepEquals(SheetOf(book).Element(X + "sheetData"), SheetOf(laid.File).Element(X + "sheetData")));
        // Пробел в конце строки ячейки — данные, а не разметка: он обязан уцелеть.
        Assert.Contains("Счёт на оплату № 7 </t>", Encoding.UTF8.GetString(after[SheetPath]));
    }

    [Fact]
    public void Лист_со_своей_раскладкой_в_книге_с_другим_листом_остаётся_побайтно_тем_же()
    {
        var own = Sheet("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>", Data + Margins());
        var book = Book(Sheet(Data + Margins()), second: own);

        var laid = Apply(book);

        Assert.Equal(RenditionLayout.Applied, laid.Outcome);
        Assert.Equal(Parts(book)["xl/worksheets/sheet2.xml"], Parts(laid.File)["xl/worksheets/sheet2.xml"]);
        Assert.Equal([ExcelPageLayout.AppliedNote(PageProfile.Default, 1)], laid.Notes);
    }

    /// <summary>Скрытый лист не печатается: его не раскладывают и в пометке не считают.</summary>
    [Fact]
    public void Скрытый_лист_не_раскладывается()
    {
        var book = Book(Sheet(Data + Margins()), second: Sheet(Data + Margins()), secondState: "hidden");

        var laid = Apply(book);

        Assert.Equal(Parts(book)["xl/worksheets/sheet2.xml"], Parts(laid.File)["xl/worksheets/sheet2.xml"]);
        Assert.Equal([ExcelPageLayout.AppliedNote(PageProfile.Default, 1)], laid.Notes);
    }

    /// <summary>Лист находится по связям оглавления, а не по имени части.</summary>
    [Fact]
    public void Лист_находится_по_связям_а_не_по_имени_части()
    {
        var laid = Apply(Book(Sheet(Data + Margins()), sheetTarget: "/xl/tables/data.xml"));

        Assert.Equal(RenditionLayout.Applied, laid.Outcome);
        Assert.NotNull(XDocument.Parse(Encoding.UTF8.GetString(Parts(laid.File)["xl/tables/data.xml"]))
            .Root!.Element(X + "pageSetup"));
    }

    /// <summary>Основное пространство имён бывает с приставкой — новые элементы встают в него же.</summary>
    [Fact]
    public void Приставка_основного_пространства_имён_сохраняется()
    {
        var sheet = "<x:worksheet xmlns:x=\"" + Main + "\"><x:sheetData/><x:pageMargins left=\"0.7\" right=\"0.7\" " +
                    "top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/></x:worksheet>";

        var laid = Apply(Book(sheet));

        var text = Encoding.UTF8.GetString(Parts(laid.File)[SheetPath]);
        Assert.Contains("<x:pageSetup ", text);
        Assert.Contains("<x:sheetPr><x:pageSetUpPr fitToPage=\"1\" /></x:sheetPr>", text);
    }

    // ── Обвязка ─────────────────────────────────────────────────────────────────────────────────

    private static LaidOut Apply(byte[] book) => ExcelPageLayout.Apply(book, PageProfile.Default, CancellationToken.None);

    private static string Attr(XElement element, string name) => element.Attribute(name)?.Value ?? "(нет)";

    private static string Margins(string header = "0.3", string footer = "0.3") =>
        $"<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"{header}\" footer=\"{footer}\"/>";

    private static string Sheet(string body) => Sheet("", body);

    private static string Sheet(string head, string body) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"" + Main + "\">" + head + body + "</worksheet>";

    private static XElement SheetOf(byte[] book) => XDocument.Parse(Encoding.UTF8.GetString(Parts(book)[SheetPath])).Root!;

    private static Dictionary<string, byte[]> Parts(byte[] book)
    {
        using var zip = new ZipArchive(new MemoryStream(book), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        });
    }

    /// <summary>Книга из частей: оглавление, связи и один-два листа.</summary>
    private static byte[] Book(string sheet, string? second = null, string secondState = "visible",
        string sheetTarget = "worksheets/sheet1.xml", string workbookNs = Main, (string Name, string Xml)? extra = null)
    {
        const string rels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var sheets = "<sheet name=\"Счёт\" sheetId=\"1\" r:id=\"rId1\"/>" +
                     (second is null ? "" : $"<sheet name=\"Второй\" sheetId=\"2\" state=\"{secondState}\" r:id=\"rId2\"/>");
        var links = $"<Relationship Id=\"rId1\" Type=\"{rels}/worksheet\" Target=\"{sheetTarget}\"/>" +
                    (second is null ? "" : $"<Relationship Id=\"rId2\" Type=\"{rels}/worksheet\" Target=\"worksheets/sheet2.xml\"/>");
        var parts = new List<(string, string)>
        {
            ("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            ("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                $"<Relationship Id=\"rId1\" Type=\"{rels}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{workbookNs}\" xmlns:r=\"{rels}\"><sheets>{sheets}</sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                links + "</Relationships>"),
            (sheetTarget.StartsWith('/') ? sheetTarget[1..] : "xl/" + sheetTarget, sheet),
        };
        if (second is not null) parts.Add(("xl/worksheets/sheet2.xml", second));
        if (extra is { } more) parts.Add(more);

        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, xml) in parts)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(xml));
            }
        return output.ToArray();
    }
}
