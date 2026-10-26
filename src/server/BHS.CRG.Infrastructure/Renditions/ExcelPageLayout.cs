using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using BHS.CRG.Domain.Storage;

namespace BHS.CRG.Infrastructure.Renditions;

/// <summary>
/// Профиль печати глазами раскладки: параметры, снятые с <see cref="RenditionLayoutProfile" /> в начале
/// построения (issue #1454). Снимок, а не сам профиль: правка профиля посреди построения не должна
/// дать образ, построенный по одним параметрам и записанный под другими.
/// </summary>
/// <param name="Name">Как профиль называется человеку — в пометке образа.</param>
/// <param name="PaperSize">Код бумаги по ECMA-376 (<c>9</c> — A4).</param>
/// <param name="FitWidth">Вписать в ширину страницы; иначе — только бумага, ориентация и поля.</param>
/// <param name="AuthorPaperFirst">
/// Бумага и ориентация, записанные автором в листе, главнее профиля. Так у системного: по нему строится
/// первый вид, и бумагу, выбранную автором, он уважает. Свой профиль человек выбирает вручную — и ставит
/// свою бумагу (решение владельца, 18.10.2026): иначе «A3 альбомная» не сработала бы почти ни на одной
/// книге, потому что Excel пишет в лист A4 книжную и тогда, когда бумагу никто не выбирал. Правило — по
/// роду профиля, а не по тому, как он выбран: один и тот же профиль с тем же отпечатком обязан давать
/// один и тот же вид.
/// </param>
public sealed record PageProfile(
    string Name, int PaperSize, string Orientation, bool FitWidth, double TopMm, double BottomMm, double LeftMm, double RightMm,
    bool AuthorPaperFirst = true)
{
    /// <summary>Заводской системный: A4, книжная, вписать по ширине, поля 5 мм со всех сторон.</summary>
    public static readonly PageProfile Default = From(RenditionLayoutProfile.System());

    public static PageProfile From(RenditionLayoutProfile profile) => new(
        profile.Name,
        profile.Paper switch { PaperFormat.A3 => 8, PaperFormat.A5 => 11, _ => 9 },
        profile.Orientation == PageOrientation.Landscape ? "landscape" : "portrait",
        profile.Fit == PageFit.Width,
        profile.MarginTop, profile.MarginBottom, profile.MarginLeft, profile.MarginRight,
        AuthorPaperFirst: profile.IsSystem);
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
/// применяет. Поля признаком не считаются: Excel пишет их всегда. Ручные разрывы СТРОК признаком тоже
/// не считаются: они делят лист по высоте, а вписывание — по ширине, и одно другому не мешает (проба к
/// ревью PR #1470: разрыв соблюдён, кегль тот же 7,1, что без него).</para>
///
/// <para><b>Что подставляется</b>: вписать по ширине (<c>fitToPage=1</c>, <c>fitToWidth=1</c>,
/// <c>fitToHeight=0</c>), поля профиля; бумага и ориентация — автора, если записаны, иначе профиля
/// (у своего профиля — всегда профиля, см. <see cref="PageProfile.AuthorPaperFirst" />).
/// Бумага пишется всегда: отсутствующая по схеме — Letter (проба: лист 216×279). Профиль «не
/// вписывать» (issue #1454) ставит только бумагу, ориентацию и поля: свойства листа и атрибуты
/// вписывания автора остаются как были.</para>
///
/// <para><b>Колонтитул.</b> Нет колонтитула с текстом — поля колонтитулов ноль, все четыре поля
/// профиля. Есть — верхнее и нижнее поля и поля колонтитулов остаются автора, профиль ставит только
/// левое и правое: под полем 5 мм колонтитул на расстоянии 0,3″ от края LibreOffice не печатает ВОВСЕ —
/// в виде его нет (проба к ревью PR #1470), а сколько ему нужно места по высоте, знает только автор.
/// Ширину, ради которой профиль и нужен, дают левое и правое поля.</para>
///
/// <para><b>Потоком, а не DOM</b>: лист бывает до 100 МБ распакованным. Элементы — строго в порядке
/// схемы <c>CT_Worksheet</c>: LibreOffice 8.37 неверный порядок прощает, но Excel такую книгу чинит, а
/// следующая версия конвертера вправе не простить. Три элемента раскладки (<c>sheetPr</c>,
/// <c>pageMargins</c>, <c>pageSetup</c>) пишутся ТОЛЬКО на своём месте схемы и только по разу — из
/// того, что о них узнал осмотр листа, — а сами они в исходнике пропускаются, где бы ни стояли: так
/// книга, у которой автор поставил их не по порядку или дважды, не получает дублей.</para>
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
    private static readonly XNamespace X = Main;

    public const string FailedNote = "Раскладку по профилю применить не удалось — вид построен по файлу как есть.";

    public static string AppliedNote(PageProfile profile, int sheets)
    {
        var form = NoteForms.First(f => f.FitWidth == profile.FitWidth && f.Single == (sheets == 1));
        return $"{(form.Single ? "" : $"{SheetsHead}{sheets}")}{form.Head}{profile.Name}{form.Tail}";
    }

    /// <summary>
    /// Пометка о раскладке по профилю — <see cref="AppliedNote" /> или <see cref="FailedNote" /> (issue #1495):
    /// её показывают не строкой, а у выбора профиля, и порт видов отделяет её от прочих при чтении.
    ///
    /// <para>Узнаётся по началу и концу формы, а не разбором: имя профиля стоит посередине и может
    /// содержать что угодно, в том числе кавычки и «»: в Excel». Формы описаны здесь же, одной парой на
    /// каждую, и <see cref="AppliedNote" /> собирается из них — новая формулировка мимо пар не пройдёт.</para>
    /// </summary>
    public static bool IsLayoutNote(string note)
    {
        if (note == FailedNote) return true;
        foreach (var form in NoteForms)
        {
            var rest = note.AsSpan();
            if (!form.Single)
            {
                if (!rest.StartsWith(SheetsHead, StringComparison.Ordinal)) continue;
                rest = rest[SheetsHead.Length..];
                var digits = 0;
                while (digits < rest.Length && char.IsAsciiDigit(rest[digits])) digits++;
                if (digits == 0) continue;
                rest = rest[digits..];
            }
            if (rest.Length >= form.Head.Length + form.Tail.Length
                && rest.StartsWith(form.Head, StringComparison.Ordinal) && rest.EndsWith(form.Tail, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Начало пометки о нескольких листах — до их числа.</summary>
    private const string SheetsHead = "Листы (";

    /// <summary>
    /// Формы <see cref="AppliedNote" />: начало до имени профиля (у нескольких листов — после их числа) и
    /// конец после него.
    /// </summary>
    private static readonly (bool FitWidth, bool Single, string Head, string Tail)[] NoteForms =
    [
        (true, true, "Лист вписан в ширину страницы по профилю печати «", "»: в Excel он печатается иначе."),
        (true, false, ") вписаны в ширину страницы по профилю печати «", "»: в Excel они печатаются иначе."),
        (false, true, "Лист разложен на бумагу по профилю печати «", "»: в Excel он печатается иначе."),
        (false, false, ") разложены на бумагу по профилю печати «", "»: в Excel они печатаются иначе."),
    ];

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
            // Ни одного листа-таблицы не нашлось — это не «автор всё задал сам», а «мы книгу не поняли»:
            // нет оглавления, связи ведут мимо, в книге одни диаграммы. Назвать такую книгу своей
            // раскладкой значило бы соврать и в пометке, и в записи образа.
            if (strict || sheets.Count == 0) return Skipped(book);

            var plan = new Dictionary<string, SheetFacts>(StringComparer.Ordinal);
            using (var zip = new ZipArchive(new MemoryStream(book, writable: false), ZipArchiveMode.Read))
                foreach (var path in sheets)
                {
                    ct.ThrowIfCancellationRequested();
                    // Связь ведёт к части, которой в архиве нет, — книга битая, и раскладывать её
                    // наполовину незачем.
                    if (Entry(zip, path) is not { } entry) return Skipped(book);
                    using var part = entry.Open();
                    var facts = Inspect(part);
                    if (!facts.Own) plan[entry.FullName] = facts;
                }

            if (plan.Count == 0) return new(RenditionLayout.BookOwn, book, [], null);

            var laid = Rewrite(book, plan, profile, ct);
            return new(RenditionLayout.Applied, laid, [AppliedNote(profile, plan.Count)], Snapshot(profile, plan.Values));
        }
        catch (OperationCanceledException) { throw; }
        // Правка — необязательный шаг, и сбой любого рода (разбор, архив, запись, атрибуты) означает одно:
        // книга уходит как есть. Узкий перечень исключений обещание «отказа не бывает» не держал:
        // ошибка записи XML или даты записи архива роняла бы построение образа, который строился.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Skipped(book);
        }
    }

    private static LaidOut Skipped(byte[] book) => new(RenditionLayout.Skipped, book, [FailedNote], null);

    /// <summary>
    /// Снимок того, что ДЕЙСТВИТЕЛЬНО записано в каждый вписанный лист: бумага и ориентация бывают
    /// автора, а верх и низ — автора у листа с колонтитулом. Код профиля один на всю книгу.
    /// </summary>
    private static string Snapshot(PageProfile profile, IEnumerable<SheetFacts> sheets) => JsonSerializer.Serialize(new
    {
        profile = profile.Name,
        fit = profile.FitWidth ? "width" : "none",
        sheets = sheets.Select(facts =>
        {
            var page = Effective(facts, profile);
            return new
            {
                paperSize = page.PaperSize,
                paperWidth = page.PaperWidth,
                paperHeight = page.PaperHeight,
                orientation = page.Orientation,
                marginsMm = new
                {
                    left = Mm(page.Left), right = Mm(page.Right), top = Mm(page.Top), bottom = Mm(page.Bottom),
                },
            };
        }).ToList(),
    }, SnapshotJson);

    private static readonly JsonSerializerOptions SnapshotJson = new()
    {
        // Имя профиля — кириллицей, а не \u-последовательностями: снимок читают и глазами.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static double Mm(double inches) => Math.Round(inches * 25.4, 1);

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
        if (Entry(zip, workbook) is not { } part) return [];

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
        if (Entry(zip, rels) is not { } entry) return found;
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

    /// <summary>
    /// Часть архива по имени. Имена частей пакета регистра не различают (OPC), а <c>GetEntry</c> — различает:
    /// связь «Worksheets/Sheet1.xml» к записи «xl/worksheets/sheet1.xml» Excel открывает, а мы бы лист
    /// не нашли.
    /// </summary>
    private static ZipArchiveEntry? Entry(ZipArchive zip, string path) => zip.GetEntry(path)
        ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// Что осмотр узнал о листе: решение и всё, из чего правка потом пишет три элемента раскладки. Сами
    /// элементы малы — их держим целиком; если автор поставил элемент дважды, берётся первый.
    /// </summary>
    /// <param name="Own">Автор задал раскладку сам — лист не трогаем.</param>
    /// <param name="HeaderFooter">У листа есть колонтитул с текстом.</param>
    private sealed record SheetFacts(bool Own, bool HeaderFooter, XElement? SheetPr, XElement? Margins, XElement? Setup);

    private static SheetFacts Inspect(Stream sheet)
    {
        bool own = false, headerFooter = false;
        XElement? sheetPr = null, margins = null, setup = null;
        using var xml = XmlReader.Create(sheet, Reading);
        xml.MoveToContent();
        if (!xml.Read()) return new(false, false, null, null, null);
        while (!xml.EOF)
        {
            if (xml.NodeType != XmlNodeType.Element || xml.Depth != 1 || xml.NamespaceURI != Main)
            {
                if (xml.NodeType == XmlNodeType.Element && xml.Depth == 1) xml.Skip();
                else xml.Read();
                continue;
            }
            // Элемент читается ВСЕГДА, даже если такой уже встречался: чтение и двигает читателя дальше.
            switch (xml.LocalName)
            {
                case "sheetPr":
                    var props = (XElement)XNode.ReadFrom(xml);
                    sheetPr ??= props;
                    if (props.Descendants().Any(e => e.Name.LocalName == "pageSetUpPr"
                                                     && e.Attribute("fitToPage")?.Value is "1" or "true"))
                        own = true;
                    break;
                case "pageSetup":
                    var page = (XElement)XNode.ReadFrom(xml);
                    setup ??= page;
                    if (page.Attribute("scale")?.Value is { } scale && scale.Trim() != "100") own = true;
                    break;
                case "pageMargins":
                    var fields = (XElement)XNode.ReadFrom(xml);
                    margins ??= fields;
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
        return new(own, headerFooter, sheetPr, margins, setup);
    }

    /// <summary>Что пишется в лист. Поля — в дюймах, как в разметке.</summary>
    /// <param name="PaperSize">Код бумаги; <c>null</c> — автор задал бумагу размерами и кода у неё нет.</param>
    private sealed record Page(string? PaperSize, string? PaperWidth, string? PaperHeight, string Orientation,
        double Left, double Right, double Top, double Bottom, double Header, double Footer);

    /// <summary>
    /// Одно место, где решается, что встанет в лист, — его читают и правка, и снимок: иначе снимок
    /// писал бы профиль там, где в лист ушли бумага, ориентация или поля автора.
    /// </summary>
    private static Page Effective(SheetFacts facts, PageProfile profile)
    {
        // Свой профиль бумагу автора не читает вовсе — ни кода, ни размеров (см. AuthorPaperFirst).
        var setup = profile.AuthorPaperFirst ? facts.Setup : null;
        string? width = setup?.Attribute("paperWidth")?.Value, height = setup?.Attribute("paperHeight")?.Value;
        var custom = width is not null && height is not null;
        var paper = setup?.Attribute("paperSize")?.Value
                    ?? (custom ? null : profile.PaperSize.ToString(CultureInfo.InvariantCulture));
        var orientation = setup?.Attribute("orientation")?.Value is "portrait" or "landscape"
            ? setup.Attribute("orientation")!.Value
            : profile.Orientation;

        double left = profile.LeftMm / 25.4, right = profile.RightMm / 25.4;
        if (!facts.HeaderFooter)
            return new(paper, custom ? width : null, custom ? height : null, orientation,
                left, right, profile.TopMm / 25.4, profile.BottomMm / 25.4, 0, 0);

        // Умолчания — Excel: поле 0,75″, колонтитул 0,3″ от края.
        var margins = facts.Margins;
        return new(paper, custom ? width : null, custom ? height : null, orientation, left, right,
            Number(margins, "top") ?? 0.75, Number(margins, "bottom") ?? 0.75,
            Number(margins, "header") ?? 0.3, Number(margins, "footer") ?? 0.3);
    }

    private static double? Number(XElement? element, string name) =>
        double.TryParse(element?.Attribute(name)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    // ── Правка ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Новый архив: части листов из плана переписаны, остальные скопированы как были, в том же порядке
    /// (<c>[Content_Types].xml</c> первым — некоторые читатели на этом настаивают).
    ///
    /// <para>Часть, которая в исходнике не сжалась (картинки, уже сжатое, записанное без сжатия),
    /// второй раз не сжимается: архив живёт до конвертера на той же машине, и тратить на него время
    /// процессора незачем. Перепаковки целиком это не отменяет — нетронутую часть <c>ZipArchive</c>
    /// скопировать сжатой не умеет.</para>
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
                var sheet = plan.TryGetValue(entry.FullName, out var facts);
                var squeezed = entry.CompressedLength < entry.Length * 9 / 10;
                var copy = target.CreateEntry(entry.FullName,
                    sheet || squeezed ? CompressionLevel.Fastest : CompressionLevel.NoCompression);
                // Дата записи бывает и вне того, что умеет архив (расширенные поля времени): копия без
                // неё ничем не хуже, а исключение отняло бы у книги раскладку.
                if (entry.LastWriteTime.Year is >= 1980 and <= 2107) copy.LastWriteTime = entry.LastWriteTime;
                using var from = entry.Open();
                using var to = copy.Open();
                if (sheet) RewriteSheet(from, to, facts!, profile);
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

        var page = Effective(facts, profile);
        bool sheetPr = false, margins = false, setup = false;
        // Пишет каждый из трёх элементов раскладки, место которого по схеме раньше ранга, — один раз.
        void Before(int rank)
        {
            if (!sheetPr && rank > SheetPrRank) { WriteSheetPr(writer, prefix, facts.SheetPr, profile.FitWidth); sheetPr = true; }
            if (!margins && rank > MarginsRank) { WriteMargins(writer, prefix, page); margins = true; }
            if (!setup && rank > SetupRank) { WriteSetup(writer, prefix, facts.Setup, page, profile); setup = true; }
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
            if (rank == SheetPrRank || rank == MarginsRank || rank == SetupRank)
            {
                // Элемент раскладки уже записан по осмотру (или запишется здесь же, на своём месте) —
                // исходный пропускаем, где бы он ни стоял и сколько бы раз ни встречался.
                Before(rank + 1);
                xml.Skip();
                continue;
            }
            if (rank >= 0) Before(rank);
            writer.WriteNode(xml, defattr: true);
        }

        Before(int.MaxValue);
        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    /// <summary>
    /// <c>sheetPr</c> с <c>pageSetUpPr/@fitToPage=1</c>. Прочие свойства листа — как были;
    /// <c>pageSetUpPr</c> по схеме последний, поэтому пишется перед закрытием, один. Профиль без
    /// вписывания свойств листа не трогает: есть — пишутся как были, нет — не появляются.
    /// </summary>
    private static void WriteSheetPr(XmlWriter writer, string prefix, XElement? props, bool fitWidth)
    {
        if (!fitWidth)
        {
            if (props is null) return;
            writer.WriteStartElement(prefix, "sheetPr", Main);
            CopyAttributes(writer, props);
            foreach (var node in props.Nodes()) node.WriteTo(writer);
            writer.WriteEndElement();
            return;
        }

        writer.WriteStartElement(prefix, "sheetPr", Main);
        XElement? fit = null;
        if (props is not null)
        {
            CopyAttributes(writer, props);
            foreach (var node in props.Nodes())
                if (node is XElement element && element.Name == X + "pageSetUpPr") fit ??= element;
                else node.WriteTo(writer);
        }
        writer.WriteStartElement(prefix, "pageSetUpPr", Main);
        if (fit is not null) CopyAttributes(writer, fit, "fitToPage");
        writer.WriteAttributeString("fitToPage", "1");
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteMargins(XmlWriter writer, string prefix, Page page)
    {
        writer.WriteStartElement(prefix, "pageMargins", Main);
        writer.WriteAttributeString("left", Format(page.Left));
        writer.WriteAttributeString("right", Format(page.Right));
        writer.WriteAttributeString("top", Format(page.Top));
        writer.WriteAttributeString("bottom", Format(page.Bottom));
        writer.WriteAttributeString("header", Format(page.Header));
        writer.WriteAttributeString("footer", Format(page.Footer));
        writer.WriteEndElement();
    }

    /// <summary>
    /// <c>pageSetup</c> с вписыванием по ширине. Атрибуты автора остаются, кроме вписывания; бумага и
    /// ориентация — из <see cref="Effective" />. Содержимого у элемента по схеме нет. Без вписывания
    /// атрибуты вписывания автора остаются как были: без <c>fitToPage</c> они не действуют.
    /// </summary>
    private static void WriteSetup(XmlWriter writer, string prefix, XElement? setup, Page page, PageProfile profile)
    {
        var fitWidth = profile.FitWidth;
        writer.WriteStartElement(prefix, "pageSetup", Main);
        if (setup is not null)
            CopyAttributes(writer, setup,
            [
                "orientation", "paperSize",
                .. fitWidth ? (string[])["fitToWidth", "fitToHeight"] : [],
                .. profile.AuthorPaperFirst ? [] : (string[])["paperWidth", "paperHeight"],
            ]);
        if (page.PaperSize is not null) writer.WriteAttributeString("paperSize", page.PaperSize);
        writer.WriteAttributeString("orientation", page.Orientation);
        if (fitWidth)
        {
            writer.WriteAttributeString("fitToWidth", "1");
            writer.WriteAttributeString("fitToHeight", "0");
        }
        writer.WriteEndElement();
    }

    /// <summary>
    /// Атрибуты элемента автора — с их пространствами имён и объявлениями, кроме названных. Объявление
    /// пространства по умолчанию не копируется: имя элемента пишем мы, и пространство у него наше.
    /// </summary>
    private static void CopyAttributes(XmlWriter writer, XElement element, params string[] except)
    {
        foreach (var attribute in element.Attributes())
        {
            var name = attribute.Name;
            if (name.Namespace == XNamespace.None && (name.LocalName == "xmlns" || except.Contains(name.LocalName))) continue;
            if (attribute.IsNamespaceDeclaration)
                writer.WriteAttributeString("xmlns", name.LocalName, XNamespace.Xmlns.NamespaceName, attribute.Value);
            else
                writer.WriteAttributeString(element.GetPrefixOfNamespace(name.Namespace), name.LocalName,
                    name.NamespaceName, attribute.Value);
        }
    }

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
