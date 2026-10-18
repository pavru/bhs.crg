using System.Buffers.Binary;
using System.Text;
using NPOI.POIFS.FileSystem;

namespace BHS.CRG.Infrastructure.Renditions;

/// <summary>
/// Области печати книги старого формата <c>.xls</c> (issue #1457): записи имён в двоичном потоке книги.
///
/// <para>⚠️ Читаются сами записи, а не книга целиком через <c>HSSFWorkbook</c>: тот знает только
/// Excel 97–2003 (BIFF8), а два из трёх настоящих <c>.xls</c> в обзоре счетов — Excel 5.0/95 (BIFF5),
/// на них он падает. Контейнер книги открывает NPOI (POIFS) — он форматов внутри не различает. Книга при
/// этом не пересохраняется: пересохранение через NPOI теряет рисунки, а здесь только чтение.</para>
///
/// <para>Область печати — встроенное имя <c>Print_Area</c> (код 0x06) с номером листа; формула имени —
/// одна или несколько ссылок на прямоугольник. Имя «Print_Area», записанное обычным, не встроенным,
/// LibreOffice не исполняет (проверено живьём) — и сверка его не берёт: иначе прятала бы текст, который на
/// странице есть. Всё, что понять не удалось, пропускается — лист тогда сверяется целиком, как без
/// области.</para>
///
/// <para>Читается только общая часть книги, до её конца: имена и листы лежат там, а дальше — ячейки и
/// рисунки, у настоящих счетов это мегабайты картинок печати.</para>
/// </summary>
internal sealed partial class ExcelPrintAreas
{
    private const ushort Bof = 0x0809, Eof = 0x000A, FilePass = 0x002F, CodePage = 0x0042, BoundSheet = 0x0085,
        NameRecord = 0x0018;
    private const ushort Biff8 = 0x0600, Biff5 = 0x0500;
    private const byte PrintAreaCode = 0x06;

    /// <summary>Кодировка имён BIFF5 без записи о ней — та же, что берёт разборщик ячеек по умолчанию.</summary>
    private const int DefaultCodePage = 1252;

    private static ExcelPrintAreas OfBiff(byte[] file)
    {
        var sheets = new List<string>();
        var byIndex = new Dictionary<int, List<Box>>();
        try
        {
            var container = new POIFSFileSystem(new MemoryStream(file, writable: false));
            var name = container.Root.EntryNames.FirstOrDefault(entry =>
                entry.Equals("Workbook", StringComparison.OrdinalIgnoreCase) || entry.Equals("Book", StringComparison.OrdinalIgnoreCase));
            if (name is null) return None;
            using var stream = container.CreateDocumentInputStream(name);

            var version = (ushort)0;
            var encoding = Encoding.GetEncoding(DefaultCodePage);
            var head = new byte[4];
            var data = new byte[ushort.MaxValue];
            while (stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4)
            {
                var id = BinaryPrimitives.ReadUInt16LittleEndian(head);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2));
                if (stream.ReadAtLeast(data.AsSpan(0, length), length, throwOnEndOfStream: false) < length) break;
                var body = data.AsSpan(0, length);

                if (version == 0)
                {
                    // Первая запись — начало книги; по ней видно, BIFF5 это или BIFF8. Иное — не книга.
                    if (id != Bof || body.Length < 2) return None;
                    version = BinaryPrimitives.ReadUInt16LittleEndian(body);
                    if (version is not (Biff8 or Biff5)) return None;
                    continue;
                }
                // Конец общей части книги: дальше — листы, имён там нет. Шифрованную книгу не читаем:
                // её записи после этой — каша, а о пароле скажет разборщик ячеек.
                if (id is Eof or FilePass) break;
                switch (id)
                {
                    case CodePage when body.Length >= 2 && version == Biff5:
                        encoding = Legacy(BinaryPrimitives.ReadUInt16LittleEndian(body));
                        break;
                    case BoundSheet:
                        sheets.Add(SheetName(body, version == Biff8, encoding) ?? "");
                        break;
                    case NameRecord when PrintArea(body, version == Biff8) is ({ } sheet, { } boxes):
                        byIndex[sheet] = boxes;
                        break;
                }
            }
        }
        // Чужой файл ломает разборщик контейнера чем угодно; для сверки это одно: области нет.
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return None;
        }
        return Named(sheets, byIndex);
    }

    /// <summary>Кодировка однобайтовых строк BIFF5 по записи книги; незнакомая — по умолчанию.</summary>
    private static Encoding Legacy(int codePage)
    {
        try { return Encoding.GetEncoding(codePage == 32769 ? DefaultCodePage : codePage); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return Encoding.GetEncoding(DefaultCodePage); }
    }

    /// <summary>
    /// Имя листа из записи о нём: смещение (4), состояние (1), вид (1), длина (1), затем буквы — у BIFF8
    /// с байтом признаков (буква в один или два байта), у BIFF5 — байтами кодировки книги.
    /// </summary>
    private static string? SheetName(ReadOnlySpan<byte> data, bool biff8, Encoding encoding)
    {
        if (data.Length < 7) return null;
        var length = data[6];
        if (!biff8) return data.Length < 7 + length ? null : encoding.GetString(data.Slice(7, length));
        if (data.Length < 8) return null;
        var wide = (data[7] & 0x01) != 0;
        var bytes = wide ? length * 2 : length;
        if (data.Length < 8 + bytes) return null;
        return wide ? Encoding.Unicode.GetString(data.Slice(8, bytes)) : Encoding.Latin1.GetString(data.Slice(8, bytes));
    }

    /// <summary>
    /// Запись имени → номер листа (с нуля) и прямоугольники, если это область печати листа.
    /// Раскладка записи у BIFF5 и BIFF8 одна, разнится только запись самого имени.
    /// </summary>
    private static (int? Sheet, List<Box>? Boxes) PrintArea(ReadOnlySpan<byte> data, bool biff8)
    {
        if (data.Length < 15) return (null, null);
        var builtIn = (BinaryPrimitives.ReadUInt16LittleEndian(data) & 0x0020) != 0;
        var nameLength = data[3];
        var formulaLength = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        var sheet = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        if (!builtIn || sheet == 0 || nameLength != 1) return (null, null);

        // У BIFF8 перед буквами имени — байт признаков (буква в один или два байта).
        var name = 14;
        var wide = false;
        if (biff8) { wide = (data[name] & 0x01) != 0; name++; }
        var formula = name + (wide ? 2 : 1);
        if (formula + formulaLength > data.Length || data[name] != PrintAreaCode) return (null, null);
        return Boxes(data.Slice(formula, formulaLength), biff8) is { } boxes ? (sheet - 1, boxes) : (null, null);
    }

    /// <summary>
    /// Формула имени → прямоугольники. Понимаются ссылка и диапазон — на лист и без него — и их
    /// объединение: так Excel и LibreOffice пишут область печати, и обе записи LibreOffice исполняет.
    /// Встретилось что-то иное — <c>null</c>: не понято.
    /// </summary>
    private static List<Box>? Boxes(ReadOnlySpan<byte> formula, bool biff8)
    {
        var boxes = new List<Box>();
        for (var at = 0; at < formula.Length;)
        {
            var token = formula[at++];
            // У ссылок класс (ссылка, значение, массив) записан двумя битами; область от него не зависит.
            var kind = token >= 0x20 ? (byte)((token & 0x1F) | 0x20) : token;
            // Ссылка на лист отличается от простой только префиксом листа: BIFF8 — 2 байта, BIFF5 — 14.
            var sheetPrefix = kind is 0x3A or 0x3B ? (biff8 ? 2 : 14) : 0;
            switch (kind)
            {
                case 0x10 or 0x15: // объединение, скобки
                    break;
                case 0x29: // начало списка ссылок: длина списка
                    at += 2;
                    break;
                case 0x25 or 0x3B: // диапазон
                    if (!Area(formula, ref at, sheetPrefix, biff8, out var area)) return null;
                    boxes.Add(area);
                    break;
                case 0x24 or 0x3A: // ячейка
                    if (!Cell(formula, ref at, sheetPrefix, biff8, out var cell)) return null;
                    boxes.Add(cell);
                    break;
                default:
                    return null;
            }
        }
        return boxes.Count > 0 ? boxes : null;
    }

    // BIFF8: строки (2+2), колонки (2+2) — в колонке два старших бита признаки «$».
    // BIFF5: строки (2+2) — признаки в двух старших битах строки, колонки (1+1).
    private static bool Area(ReadOnlySpan<byte> formula, ref int at, int sheetPrefix, bool biff8, out Box box)
    {
        box = default;
        var size = sheetPrefix + (biff8 ? 8 : 6);
        if (at + size > formula.Length) return false;
        var span = formula.Slice(at + sheetPrefix, size - sheetPrefix);
        at += size;
        box = biff8
            ? new Box(U16(span, 0), U16(span, 4) & 0x3FFF, U16(span, 2), U16(span, 6) & 0x3FFF)
            : new Box(U16(span, 0) & 0x3FFF, span[4], U16(span, 2) & 0x3FFF, span[5]);
        return true;
    }

    private static bool Cell(ReadOnlySpan<byte> formula, ref int at, int sheetPrefix, bool biff8, out Box box)
    {
        box = default;
        var size = sheetPrefix + (biff8 ? 4 : 3);
        if (at + size > formula.Length) return false;
        var span = formula.Slice(at + sheetPrefix, size - sheetPrefix);
        at += size;
        var (row, column) = biff8 ? (U16(span, 0), U16(span, 2) & 0x3FFF) : (U16(span, 0) & 0x3FFF, (int)span[2]);
        box = new Box(row, column, row, column);
        return true;
    }

    private static int U16(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt16LittleEndian(span[at..]);
}
