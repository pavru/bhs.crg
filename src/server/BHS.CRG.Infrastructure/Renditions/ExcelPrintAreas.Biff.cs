using System.Buffers.Binary;
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
/// одна или несколько ссылок на прямоугольник. Всё, что понять не удалось, пропускается — лист тогда
/// сверяется целиком, как без области.</para>
/// </summary>
internal sealed partial class ExcelPrintAreas
{
    private const ushort Bof = 0x0809, Eof = 0x000A, FilePass = 0x002F, NameRecord = 0x0018;
    private const ushort Biff8 = 0x0600, Biff5 = 0x0500;
    private const byte PrintAreaCode = 0x06;

    private static ExcelPrintAreas OfBiff(byte[] file)
    {
        var areas = new ExcelPrintAreas();
        try
        {
            if (WorkbookStream(file) is not { } stream) return areas;
            var version = (ushort)0;
            for (var at = 0; at + 4 <= stream.Length;)
            {
                var id = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(at));
                var length = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(at + 2));
                if (at + 4 + length > stream.Length) break;
                var data = stream.AsSpan(at + 4, length);
                at += 4 + length;

                if (version == 0)
                {
                    // Первая запись — начало книги; по ней видно, BIFF5 это или BIFF8. Иное — не книга.
                    if (id != Bof || data.Length < 2) return areas;
                    version = BinaryPrimitives.ReadUInt16LittleEndian(data);
                    if (version is not (Biff8 or Biff5)) return areas;
                    continue;
                }
                // Конец общей части книги: дальше — листы, имён там нет. Шифрованную книгу не читаем:
                // её записи после этой — каша, а о пароле скажет разборщик ячеек.
                if (id is Eof or FilePass) break;
                if (id == NameRecord && PrintArea(data, version == Biff8) is ({ } sheet, { } boxes))
                    areas._bySheet[sheet] = boxes;
            }
        }
        // Чужой файл ломает разборщик контейнера чем угодно; для сверки это одно: области нет.
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
        }
        return areas;
    }

    /// <summary>Поток книги внутри контейнера: «Workbook» у BIFF8, «Book» у BIFF5.</summary>
    private static byte[]? WorkbookStream(byte[] file)
    {
        var container = new POIFSFileSystem(new MemoryStream(file, writable: false));
        var name = container.Root.EntryNames.FirstOrDefault(entry =>
            entry.Equals("Workbook", StringComparison.OrdinalIgnoreCase) || entry.Equals("Book", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var stream = container.CreateDocumentInputStream(name);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
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
    /// Формула имени → прямоугольники. Понимаются ссылка и диапазон на лист и их объединение — так
    /// Excel и LibreOffice пишут область печати. Встретилось что-то иное — <c>null</c>: не понято.
    /// </summary>
    private static List<Box>? Boxes(ReadOnlySpan<byte> formula, bool biff8)
    {
        var boxes = new List<Box>();
        for (var at = 0; at < formula.Length;)
        {
            var token = formula[at++];
            // У ссылок класс (ссылка, значение, массив) записан двумя битами; область от него не зависит.
            var kind = token >= 0x20 ? (byte)((token & 0x1F) | 0x20) : token;
            switch (kind)
            {
                case 0x10 or 0x15: // объединение, скобки
                    break;
                case 0x29: // начало списка ссылок: длина списка
                    at += 2;
                    break;
                case 0x3B: // диапазон на лист
                    if (!Area(formula, ref at, biff8, out var area)) return null;
                    boxes.Add(area);
                    break;
                case 0x3A: // ячейка на лист
                    if (!Cell(formula, ref at, biff8, out var cell)) return null;
                    boxes.Add(cell);
                    break;
                default:
                    return null;
            }
        }
        return boxes.Count > 0 ? boxes : null;
    }

    // BIFF8: лист (2), строки (2+2), колонки (2+2) — в колонке два старших бита признаки «$».
    // BIFF5: лист (2+8+2+2), строки (2+2) — признаки в двух старших битах строки, колонки (1+1).
    private static bool Area(ReadOnlySpan<byte> formula, ref int at, bool biff8, out Box box)
    {
        box = default;
        var size = biff8 ? 10 : 20;
        if (at + size > formula.Length) return false;
        var span = formula.Slice(at, size);
        at += size;
        box = biff8
            ? new Box(U16(span, 2), U16(span, 6) & 0x3FFF, U16(span, 4), U16(span, 8) & 0x3FFF)
            : new Box(U16(span, 14) & 0x3FFF, span[18], U16(span, 16) & 0x3FFF, span[19]);
        return true;
    }

    private static bool Cell(ReadOnlySpan<byte> formula, ref int at, bool biff8, out Box box)
    {
        box = default;
        var size = biff8 ? 6 : 17;
        if (at + size > formula.Length) return false;
        var span = formula.Slice(at, size);
        at += size;
        var (row, column) = biff8 ? (U16(span, 2), U16(span, 4) & 0x3FFF) : (U16(span, 14) & 0x3FFF, (int)span[16]);
        box = new Box(row, column, row, column);
        return true;
    }

    private static int U16(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt16LittleEndian(span[at..]);
}
