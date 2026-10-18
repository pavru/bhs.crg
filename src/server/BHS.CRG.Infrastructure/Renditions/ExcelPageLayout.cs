using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using BHS.CRG.Domain.Storage;

namespace BHS.CRG.Infrastructure.Renditions;

/// <summary>
/// Профиль печати: как разложить на бумаге лист книги, автор которого раскладку не задал (issue #1453).
/// Пока один, константой; профили администратора — #1454.
/// </summary>
/// <param name="Title">Как профиль называется человеку — в пометке образа.</param>
/// <param name="PaperSize">Код бумаги по ECMA-376 (<c>9</c> — A4).</param>
public sealed record PageProfile(string Code, string Title, int PaperSize, string Orientation, double MarginMm)
{
    /// <summary>A4, книжная, вписать по ширине, поля 5 мм со всех сторон.</summary>
    public static readonly PageProfile Default = new("default", "по умолчанию", 9, "portrait", 5);
}

/// <summary>Книга после раскладки: что ушло к конвертеру и что сказать о нём.</summary>
/// <param name="File">Байты для конвертера. У <see cref="RenditionLayout.BookOwn" /> и
/// <see cref="RenditionLayout.Skipped" /> — ТОТ ЖЕ массив, что пришёл: книгу со своей раскладкой и книгу,
/// которую подправить не удалось, конвертер получает как есть.</param>
/// <param name="Applied">Снимок применённых параметров (JSON) — только у <see cref="RenditionLayout.Applied" />.</param>
public sealed record LaidOut(RenditionLayout Outcome, byte[] File, IReadOnlyList<string> Notes, string? Applied);

/// <summary>
/// Раскладка книги <c>.xlsx</c> по профилю печати (issue #1453): правило «автор задал раскладку сам» и
/// правка КОПИИ книги перед конвертером.
///
/// <para><b>Почему правка копии, а не параметр конвертера.</b> У Gotenberg параметров «бумага, поля,
/// вписать» для таблиц нет. Есть <c>singlePageSheets</c>, но он подгоняет страницу под лист — бумага
/// выходит 278×77 мм, а масштаб автора не уважается (проба #1453 (в)). LibreOffice же исполняет
/// раскладку, записанную в самой книге, — её и подставляем.</para>
///
/// <para><b>Правило — по каждому листу-таблице.</b> Своя раскладка у листа, если есть хоть одно:
/// вписывание (<c>sheetPr/pageSetUpPr/@fitToPage</c>), масштаб не 100 (<c>pageSetup/@scale</c>), ручной
/// разрыв колонок (<c>colBreaks/brk</c>). <c>fitToWidth</c>/<c>fitToHeight</c> БЕЗ <c>fitToPage</c>
/// признаком не считаются: LibreOffice их без флага не исполняет — проба #1453 (а): такая книга
/// выходит той же, что книга без раскладки, две страницы, кегль 10. Excel без флага их тоже не
/// применяет. Поля признаком не считаются: Excel пишет их всегда.</para>
///
/// <para><b>Что подставляется</b>: вписать по ширине (<c>fitToPage=1</c>, <c>fitToWidth=1</c>,
/// <c>fitToHeight=0</c>), поля профиля; бумага и ориентация — автора, если записаны, иначе профиля.
/// Бумага пишется всегда: отсутствующая по схеме — Letter (проба: лист 216×279). Поля колонтитулов —
/// ноль, если колонтитула нет: под нулевым полем стоящий колонтитул лёг бы на текст.</para>
///
/// <para><b>Потоком, а не DOM</b>: лист бывает до 100 МБ распакованным. Элементы — строго в порядке
/// схемы <c>CT_Worksheet</c>: LibreOffice 8.37 неверный порядок прощает, но Excel такую книгу чинит, а
/// следующая версия конвертера вправе не простить.</para>
///
/// <para>Профиль не делает из книги отказ: что пошло не так — книга идёт к конвертеру как есть, с
/// пометкой.</para>
/// </summary>
public static class ExcelPageLayout
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Strict = "http://purl.oclc.org/ooxml/spreadsheetml/main";
    private const string Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Compatibility = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    public const string FailedNote = "Раскладку по профилю применить не удалось — вид построен по файлу как есть.";

    public static string AppliedNote(PageProfile profile, int sheets) => sheets == 1
        ? $"Лист вписан в ширину страницы по профилю печати «{profile.Title}»: в Excel он печатается иначе."
        : $"Листы ({sheets}) вписаны в ширину страницы по профилю печати «{profile.Title}»: в Excel они печатаются иначе.";

    /// <summary>Порядок элементов листа по схеме <c>CT_Worksheet</c>: вставлять недостающее можно только сюда.</summary>
    private static readonly string[] Order =
    [
        "sheetPr", "dimension", "sheetViews", "sheetFormatPr", "cols", "sheetData", "sheetCalcPr", "sheetProtection",
        "protectedRanges", "scenarios", "autoFilter", "sortState", "dataConsolidate", "customSheetViews", "mergeCells",
        "phoneticPr", "conditionalFormatting", "dataValidations", "hyperlinks", "printOptions", "pageMargins",
        "pageSetup", "headerFooter", "rowBreaks", "colBreaks", "customProperties", "cellWatches", "ignoredErrors",
        "smartTags", "drawing", "legacyDrawing", "legacyDrawingHF", "drawingHF", "picture", "oleObjects", "controls",
        "webPublishItems", "tableParts", "extLst",
    ];

    private static readonly int SheetPrRank = Rank("sheetPr");
    private static readonly int MarginsRank = Rank("pageMargins");
    private static readonly int SetupRank = Rank("pageSetup");

    /// <summary>
    /// <c>mc:AlternateContent</c> на уровне листа оборачивает элементы управления и объекты OLE — они
    /// стоят после <c>pageSetup</c>. Своего места в схеме у обёртки нет, и без ранга недостающая
    /// раскладка встала бы после неё.
    /// </summary>
    private static readonly int AlternateRank = Rank("oleObjects");

    private static int Rank(string name) => Array.IndexOf(Order, name);

    private static readonly XmlReaderSettings Reading = new()
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false,
    };

    /// <summary>Разложить книгу по профилю. Отказа не бывает: в худшем случае — та же книга с пометкой.</summary>
    public static LaidOut Apply(byte[] book, PageProfile profile, CancellationToken ct)
    {
        try
        {
            var sheets = SheetsOf(book, out var strict);
            if (strict) return new(RenditionLayout.Skipped, book, [FailedNote], null);

            var plan = new Dictionary<string, SheetFacts>(StringComparer.Ordinal);
            using (var zip = new ZipArchive(new MemoryStream(book, writable: false), ZipArchiveMode.Read))
                foreach (var path in sheets)
                {
                    ct.ThrowIfCancellationRequested();
                    if (zip.GetEntry(path) is not { } entry) continue;
                    using var part = entry.Open();
                    var facts = Inspect(part);
                    if (!facts.Own) plan[path] = facts;
                }

            if (plan.Count == 0) return new(RenditionLayout.BookOwn, book, [], null);

            var laid = Rewrite(book, plan, profile, ct);
            return new(RenditionLayout.Applied, laid, [AppliedNote(profile, plan.Count)], Snapshot(profile, plan.Count));
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException or FormatException)
        {
            return new(RenditionLayout.Skipped, book, [FailedNote], null);
        }
    }

    private static string Snapshot(PageProfile profile, int sheets) => JsonSerializer.Serialize(new
    {
        profile = profile.Code,
        paperSize = profile.PaperSize,
        orientation = profile.Orientation,
        fit = "width",
        marginMm = profile.MarginMm,
        sheets,
    });

    // ── Листы книги ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Части видимых листов-таблиц — через оглавление книги и его связи, а не по имени
    /// <c>sheetN.xml</c>: имя части ничего не обещает, а лист диаграммы раскладку по ширине не понимает
    /// вовсе.
    /// </summary>
    private static List<string> SheetsOf(byte[] book, out bool strict)
    {
        strict = false;
        using var zip = new ZipArchive(new MemoryStream(book, writable: false), ZipArchiveMode.Read);
        var workbook = Target(zip, "_rels/.rels", "", rel => rel.EndsWith("/officeDocument", StringComparison.Ordinal))
            .FirstOrDefault() ?? "xl/workbook.xml";
        if (zip.GetEntry(workbook) is not { } part) return [];

        var ids = new List<string>();
        using (var stream = part.Open())
        using (var xml = XmlReader.Create(stream, Reading))
        {
            xml.MoveToContent();
            if (xml.NamespaceURI == Strict) { strict = true; return []; }
            while (xml.Read())
                // Скрытый лист не печатается: раскладывать его незачем, а в пометке «листы вписаны» он
                // считался бы листом, которого человек в виде не увидит.
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "sheet" && xml.NamespaceURI == Main
                    && xml.GetAttribute("state") is not ("hidden" or "veryHidden")
                    && xml.GetAttribute("id", OfficeRels) is { } id)
                    ids.Add(id);
        }

        var folder = workbook.Contains('/') ? workbook[..(workbook.LastIndexOf('/') + 1)] : "";
        var rels = $"{folder}_rels/{workbook[folder.Length..]}.rels";
        var byId = Targets(zip, rels, folder, rel => rel.EndsWith("/worksheet", StringComparison.Ordinal));
        return [.. ids.Where(byId.ContainsKey).Select(id => byId[id])];
    }

    private static IEnumerable<string> Target(ZipArchive zip, string rels, string folder, Func<string, bool> type)
        => Targets(zip, rels, folder, type).Values;

    /// <summary>Связи части: идентификатор → путь части в архиве. Внешние ссылки пропускаются.</summary>
    private static Dictionary<string, string> Targets(ZipArchive zip, string rels, string folder, Func<string, bool> type)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (zip.GetEntry(rels) is not { } entry) return found;
        using var stream = entry.Open();
        using var xml = XmlReader.Create(stream, Reading);
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "Relationship" || xml.NamespaceURI != Relationships) continue;
            if (xml.GetAttribute("TargetMode") == "External") continue;
            if (xml.GetAttribute("Type") is not { } kind || !type(kind)) continue;
            if (xml.GetAttribute("Id") is not { } id || xml.GetAttribute("Target") is not { } target) continue;
            found[id] = Resolve(folder, target);
        }
        return found;
    }

    /// <summary>Путь цели связи в архиве: от корня пакета, если начинается с «/», иначе от каталога части.</summary>
    private static string Resolve(string folder, string target)
    {
        var parts = new List<string>();
        foreach (var piece in (target.StartsWith('/') ? target[1..] : folder + target).Split('/'))
        {
            if (piece is "" or ".") continue;
            if (piece == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(piece);
        }
        return string.Join('/', parts);
    }

    // ── Правило ─────────────────────────────────────────────────────────────────────────────────

    /// <param name="Own">Автор задал раскладку сам — лист не трогаем.</param>
    /// <param name="HeaderFooter">У листа есть колонтитул с текстом: его полю ноль ставить нельзя.</param>
    private sealed record SheetFacts(bool Own, bool HeaderFooter, double? Header, double? Footer);

    private static SheetFacts Inspect(Stream sheet)
    {
        bool own = false, headerFooter = false;
        double? header = null, footer = null;
        using var xml = XmlReader.Create(sheet, Reading);
        xml.MoveToContent();
        if (!xml.Read()) return new(false, false, null, null);
        while (!xml.EOF)
        {
            if (xml.NodeType != XmlNodeType.Element || xml.Depth != 1 || xml.NamespaceURI != Main)
            {
                if (xml.NodeType == XmlNodeType.Element && xml.Depth == 1) xml.Skip();
                else xml.Read();
                continue;
            }
            switch (xml.LocalName)
            {
                case "sheetPr":
                    using (var inner = xml.ReadSubtree())
                        while (inner.Read())
                            if (inner.NodeType == XmlNodeType.Element && inner.LocalName == "pageSetUpPr"
                                && inner.GetAttribute("fitToPage") is "1" or "true")
                                own = true;
                    xml.Read();
                    break;
                case "pageSetup":
                    if (xml.GetAttribute("scale") is { } scale && scale.Trim() != "100") own = true;
                    xml.Skip();
                    break;
                case "pageMargins":
                    header = Number(xml.GetAttribute("header"));
                    footer = Number(xml.GetAttribute("footer"));
                    xml.Skip();
                    break;
                case "colBreaks":
                    using (var inner = xml.ReadSubtree())
                        while (inner.Read())
                            if (inner.NodeType == XmlNodeType.Element && inner.LocalName == "brk") own = true;
                    xml.Read();
                    break;
                case "headerFooter":
                    using (var inner = xml.ReadSubtree())
                        while (inner.Read())
                            if (inner.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && inner.Value.Trim().Length > 0)
                                headerFooter = true;
                    xml.Read();
                    break;
                default:
                    xml.Skip();
                    break;
            }
        }
        return new(own, headerFooter, header, footer);
    }

    private static double? Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    // ── Правка ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Новый архив: части листов из плана переписаны, остальные скопированы как были, в том же порядке
    /// (<c>[Content_Types].xml</c> первым — некоторые читатели на этом настаивают).
    /// </summary>
    private static byte[] Rewrite(byte[] book, Dictionary<string, SheetFacts> plan, PageProfile profile, CancellationToken ct)
    {
        using var output = new MemoryStream(book.Length + 4096);
        using (var source = new ZipArchive(new MemoryStream(book, writable: false), ZipArchiveMode.Read))
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var copy = target.CreateEntry(entry.FullName, CompressionLevel.Fastest);
                copy.LastWriteTime = entry.LastWriteTime;
                using var from = entry.Open();
                using var to = copy.Open();
                if (plan.TryGetValue(entry.FullName, out var facts)) RewriteSheet(from, to, facts, profile);
                else from.CopyTo(to);
            }
        }
        return output.ToArray();
    }

    private static void RewriteSheet(Stream from, Stream to, SheetFacts facts, PageProfile profile)
    {
        using var xml = XmlReader.Create(from, Reading);
        using var writer = XmlWriter.Create(to, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None,
            NamespaceHandling = NamespaceHandling.Default,
        });

        writer.WriteStartDocument(standalone: true);
        xml.MoveToContent();
        if (xml.NamespaceURI != Main || xml.LocalName != "worksheet")
            throw new XmlException("часть листа — не лист таблицы");
        var prefix = xml.Prefix;
        writer.WriteStartElement(prefix, "worksheet", Main);
        writer.WriteAttributes(xml, defattr: true);
        xml.MoveToElement();

        bool sheetPr = false, margins = false, setup = false;
        void Before(int rank)
        {
            if (!sheetPr && rank > SheetPrRank) { WriteSheetPr(writer, prefix, null); sheetPr = true; }
            if (!margins && rank > MarginsRank) { WriteMargins(writer, prefix, facts, profile); margins = true; }
            if (!setup && rank > SetupRank) { WriteSetup(writer, prefix, null, profile); setup = true; }
        }

        if (xml.IsEmptyElement)
        {
            Before(int.MaxValue);
            writer.WriteEndElement();
            return;
        }

        xml.Read();
        while (!(xml.NodeType == XmlNodeType.EndElement && xml.Depth == 0))
        {
            if (xml.NodeType != XmlNodeType.Element)
            {
                writer.WriteNode(xml, defattr: true);
                continue;
            }

            var rank = xml.NamespaceURI == Main ? Rank(xml.LocalName)
                : xml.NamespaceURI == Compatibility && xml.LocalName == "AlternateContent" ? AlternateRank : -1;
            if (rank >= 0) Before(rank);

            if (xml.NamespaceURI == Main && xml.LocalName == "sheetPr") { WriteSheetPr(writer, prefix, xml); sheetPr = true; }
            else if (xml.NamespaceURI == Main && xml.LocalName == "pageMargins")
            {
                WriteMargins(writer, prefix, facts, profile);
                margins = true;
                xml.Skip();
            }
            else if (xml.NamespaceURI == Main && xml.LocalName == "pageSetup") { WriteSetup(writer, prefix, xml, profile); setup = true; }
            else writer.WriteNode(xml, defattr: true);
        }

        Before(int.MaxValue);
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>
    /// <c>sheetPr</c> с <c>pageSetUpPr/@fitToPage=1</c>. Прочие свойства листа — как были;
    /// <c>pageSetUpPr</c> по схеме последний, поэтому недостающий встаёт перед закрытием.
    /// </summary>
    private static void WriteSheetPr(XmlWriter writer, string prefix, XmlReader? xml)
    {
        writer.WriteStartElement(prefix, "sheetPr", Main);
        if (xml is null)
        {
            WriteFit(writer, prefix, null);
            writer.WriteEndElement();
            return;
        }

        CopyAttributes(writer, xml, skip: null);
        if (xml.IsEmptyElement)
        {
            WriteFit(writer, prefix, null);
            writer.WriteEndElement();
            xml.Read();
            return;
        }

        var fit = false;
        xml.Read();
        while (!(xml.NodeType == XmlNodeType.EndElement && xml.Depth == 1))
        {
            if (xml.NodeType == XmlNodeType.Element && xml.NamespaceURI == Main && xml.LocalName == "pageSetUpPr")
            {
                WriteFit(writer, prefix, xml);
                fit = true;
                xml.Skip();
            }
            else writer.WriteNode(xml, defattr: true);
        }
        if (!fit) WriteFit(writer, prefix, null);
        writer.WriteEndElement();
        xml.Read();
    }

    private static void WriteFit(XmlWriter writer, string prefix, XmlReader? existing)
    {
        writer.WriteStartElement(prefix, "pageSetUpPr", Main);
        if (existing is not null) CopyAttributes(writer, existing, skip: "fitToPage");
        writer.WriteAttributeString("fitToPage", "1");
        writer.WriteEndElement();
    }

    private static void WriteMargins(XmlWriter writer, string prefix, SheetFacts facts, PageProfile profile)
    {
        var inches = Inches(profile.MarginMm);
        writer.WriteStartElement(prefix, "pageMargins", Main);
        foreach (var side in new[] { "left", "right", "top", "bottom" }) writer.WriteAttributeString(side, inches);
        // Колонтитул с текстом остаётся на своём поле (а нет своего — на поле Excel по умолчанию).
        writer.WriteAttributeString("header", facts.HeaderFooter ? Format(facts.Header ?? 0.3) : "0");
        writer.WriteAttributeString("footer", facts.HeaderFooter ? Format(facts.Footer ?? 0.3) : "0");
        writer.WriteEndElement();
    }

    /// <summary>
    /// <c>pageSetup</c> с вписыванием по ширине. Атрибуты автора остаются, кроме вписывания; бумага и
    /// ориентация — его, если записаны, иначе профиля.
    /// </summary>
    private static void WriteSetup(XmlWriter writer, string prefix, XmlReader? xml, PageProfile profile)
    {
        writer.WriteStartElement(prefix, "pageSetup", Main);
        var paper = false;
        var orientation = false;
        if (xml is not null)
        {
            paper = xml.GetAttribute("paperSize") is not null
                    || (xml.GetAttribute("paperWidth") is not null && xml.GetAttribute("paperHeight") is not null);
            orientation = xml.GetAttribute("orientation") is "portrait" or "landscape";
            CopyAttributes(writer, xml, skip: null, except: ["fitToWidth", "fitToHeight", "orientation"]);
            if (orientation) writer.WriteAttributeString("orientation", xml.GetAttribute("orientation"));
        }
        if (!paper) writer.WriteAttributeString("paperSize", profile.PaperSize.ToString(CultureInfo.InvariantCulture));
        if (!orientation) writer.WriteAttributeString("orientation", profile.Orientation);
        writer.WriteAttributeString("fitToWidth", "1");
        writer.WriteAttributeString("fitToHeight", "0");
        writer.WriteEndElement();
        // Содержимого у pageSetup по схеме нет — пропускаем элемент целиком, сколько бы в нём ни было.
        xml?.Skip();
    }

    /// <summary>Атрибуты элемента, на котором стоит читатель, — с их пространствами имён.</summary>
    private static void CopyAttributes(XmlWriter writer, XmlReader xml, string? skip, string[]? except = null)
    {
        if (!xml.MoveToFirstAttribute()) return;
        do
        {
            if (xml.LocalName == skip && xml.NamespaceURI.Length == 0) continue;
            if (except is not null && xml.NamespaceURI.Length == 0 && except.Contains(xml.LocalName)) continue;
            writer.WriteAttributeString(xml.Prefix, xml.LocalName, xml.NamespaceURI, xml.Value);
        }
        while (xml.MoveToNextAttribute());
        xml.MoveToElement();
    }

    private static string Inches(double mm) => Format(mm / 25.4);

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
