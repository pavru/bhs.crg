using System.IO.Compression;
using System.Xml;

namespace BHS.CRG.Infrastructure.Renditions;

/// <summary>
/// Области печати листов книги <c>.xlsx</c> (issue #1268): что из листа попадает на страницу.
///
/// <para>Нужны сверке слов. Лист со счётом в области печати и справочными колонками рядом — обычное
/// дело; на страницу идёт только область, и без неё здоровая книга выглядела бы потерявшей
/// половину текста — то есть получала бы отказ навсегда.</para>
///
/// <para>⚠️ Только новый формат. У <c>.xls</c> область печати записана в двоичном потоке книги,
/// и разборщик её не отдаёт: там сверяется весь видимый лист, и книга с большим текстом вне области
/// печати может получить пометку или отказ зря. Ограничение известное и названо в DEV_NOTES.</para>
/// </summary>
internal sealed class ExcelPrintAreas
{
    private const string Name = "_xlnm.Print_Area";

    /// <summary>Прямоугольник области, границы включительно, счёт с нуля.</summary>
    private readonly record struct Box(int Top, int Left, int Bottom, int Right);

    private readonly Dictionary<int, List<Box>> _bySheet = [];

    public static readonly ExcelPrintAreas None = new();

    /// <summary>Печатается ли ячейка. У листа без области печатается всё.</summary>
    public bool Prints(int sheet, int row, int column) =>
        !_bySheet.TryGetValue(sheet, out var boxes)
        || boxes.Exists(box => row >= box.Top && row <= box.Bottom && column >= box.Left && column <= box.Right);

    /// <summary>
    /// Области из оглавления книги. Что не разобралось — пропускается: лист тогда сверяется целиком,
    /// как если бы области у него не было.
    /// </summary>
    public static ExcelPrintAreas Of(byte[] file)
    {
        var areas = new ExcelPrintAreas();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(file, writable: false), ZipArchiveMode.Read);
            if (zip.GetEntry("xl/workbook.xml") is not { } workbook) return areas;
            using var part = workbook.Open();
            using var xml = XmlReader.Create(part, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (xml.Read())
            {
                if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "definedName") continue;
                if (xml.GetAttribute("name") != Name) continue;
                if (!int.TryParse(xml.GetAttribute("localSheetId"), out var sheet)) continue;
                var boxes = Parse(xml.ReadElementContentAsString());
                if (boxes is not null) areas._bySheet[sheet] = boxes;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException)
        {
            // Оглавление не читается — о том, цела ли книга, скажут разборщик и конвертер.
        }
        return areas;
    }

    /// <summary>«Лист1!$A$1:$F$20,Лист1!$H:$J» → прямоугольники; <c>null</c> — запись не понята.</summary>
    private static List<Box>? Parse(string formula)
    {
        var boxes = new List<Box>();
        foreach (var piece in formula.Split(','))
        {
            var range = piece[(piece.LastIndexOf('!') + 1)..].Replace("$", "").Trim();
            var ends = range.Split(':');
            if (ends.Length is < 1 or > 2 || !Cell(ends[0], out var fromRow, out var fromColumn)) return null;
            int? toRow = fromRow, toColumn = fromColumn;
            if (ends.Length == 2 && !Cell(ends[1], out toRow, out toColumn)) return null;
            // Колонки без строк («A:F») — на всю высоту листа, строки без колонок — на всю ширину.
            boxes.Add(new Box(fromRow ?? 0, fromColumn ?? 0, toRow ?? int.MaxValue, toColumn ?? int.MaxValue));
        }
        return boxes.Count > 0 ? boxes : null;
    }

    private static bool Cell(string reference, out int? row, out int? column)
    {
        row = column = null;
        var letters = 0;
        while (letters < reference.Length && char.IsAsciiLetter(reference[letters])) letters++;
        if (letters > 3 || reference.Length == 0) return false;
        if (letters > 0)
        {
            var index = 0;
            foreach (var letter in reference[..letters]) index = index * 26 + (char.ToUpperInvariant(letter) - 'A' + 1);
            column = index - 1;
        }
        if (letters == reference.Length) return true;
        if (!int.TryParse(reference[letters..], out var number) || number < 1) return false;
        row = number - 1;
        return true;
    }
}
