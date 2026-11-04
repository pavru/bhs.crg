using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using BHS.CRG.Infrastructure.DataSets;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;
using SixLabors.Fonts;

namespace BHS.CRG.Tests.DataSets;

/// <summary>
/// Выгрузка xlsx не зависит от шрифтов машины.
///
/// <para>Было: NPOI 2.7 у потокового листа меряет знак «0» шрифтом книги при сбросе первой же строки,
/// хотя автоширину никто не просил. Calibri и Arial на Linux нет, и NPOI берёт первое семейство
/// машины; на части раннеров CI им оказывался цветной Noto Color Emoji без таблицы <c>loca</c>, и
/// падала каждая выгрузка — <c>MissingFontTableException</c>. Какое семейство первое, решал порядок
/// файлов на диске, поэтому «Re-run failed jobs» проходил.</para>
///
/// <para>Системные шрифты процесса не подменить, поэтому подменяется то, что NPOI из них достаёт:
/// шрифт книги в его кэше становится шрифтом, который SixLabors описывает, но прочесть не может, —
/// ровно положение неудачного раннера, и на любой машине.</para>
/// </summary>
[Collection(NpoiFontCacheCollection.Name)]
public class SpreadsheetExporterFontTests
{
    [Fact]
    public void Xlsx_is_written_when_the_machine_font_cannot_be_read()
    {
        using var _ = UnreadableBookFont.Install();
        // Контроль: подмена действует — замер NPOI падает так же, как на неудачном раннере. Без этой
        // строки тест позеленел бы и там, где NPOI до кэша не дошёл.
        Assert.Throws<MissingFontTableException>(() => SheetUtil.GetDefaultCharWidth(new XSSFWorkbook()));

        // Строк больше окна потоковой книги (200): сброс идёт и по ходу записи, а не только в конце.
        var rows = Enumerable.Range(1, 500)
            .Select(i => (IReadOnlyList<object?>)[i, $"Позиция {i}"])
            .ToList();
        var (bytes, _, _) = SpreadsheetExporter.ExportSheets(
            SpreadsheetFormat.Xlsx, [new SpreadsheetExporter.Sheet("Лист", ["№", "Наименование"], rows)]);

        var sheet = new XSSFWorkbook(new MemoryStream(bytes)).GetSheetAt(0);
        Assert.Equal("Позиция 500", sheet.GetRow(500).GetCell(1).StringCellValue);
        // Ширина по-прежнему по содержимому — счётом знаков: «Наименование» и два знака запаса.
        Assert.Equal((12 + 2) * 256, sheet.GetColumnWidth(1));
    }

    /// <summary>
    /// Шрифт книги по умолчанию (Calibri 11) в кэше шрифтов NPOI — заменён нечитаемым. Dispose
    /// возвращает кэш как был: остальные тесты процесса меряют настоящими шрифтами.
    /// </summary>
    private sealed class UnreadableBookFont : IDisposable
    {
        private readonly IDictionary _cache;
        private readonly object _key;
        private readonly string _path;
        private readonly CultureInfo _culture;

        private UnreadableBookFont(IDictionary cache, object key, string path, CultureInfo culture)
            => (_cache, _key, _path, _culture) = (cache, key, path, culture);

        public static UnreadableBookFont Install()
        {
            var util = typeof(SheetUtil);
            const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
            var cache = (IDictionary)util.GetField("FontCache", Static)!.GetValue(null)!;
            // Кэшем NPOI пользуется, только пока культура потока — та, с которой он стартовал.
            var startup = (CultureInfo)util.GetField("StartupCulture", Static)!.GetValue(null)!;
            var keyType = util.GetNestedType("FontCacheKey", BindingFlags.NonPublic)!;

            var bookFont = new XSSFWorkbook().GetFontAt(0);
            var key = Activator.CreateInstance(keyType, bookFont.FontName, (float)bookFont.FontHeightInPoints, FontStyle.Regular)!;

            var path = Path.Combine(Path.GetTempPath(), $"bhs-unreadable-{Guid.NewGuid():N}.ttf");
            File.WriteAllBytes(path, NameOnlyFont("BHS Unreadable"));
            // Из файла, а не из потока: так SixLabors читает только описание, а сам шрифт — при первом
            // замере, как системные. Поток он прочёл бы целиком сразу и отказал бы здесь.
            var family = new FontCollection().Add(path);
            cache[key] = family.CreateFont((float)bookFont.FontHeightInPoints);

            var culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = startup;
            return new UnreadableBookFont(cache, key, path, culture);
        }

        public void Dispose()
        {
            _cache.Remove(_key);
            CultureInfo.CurrentCulture = _culture;
            try { File.Delete(_path); } catch (IOException) { }
        }

        /// <summary>
        /// TrueType-файл из одной таблицы <c>name</c>: описание шрифта (семейство, начертание) читается,
        /// а сам шрифт — нет: таблиц очертаний в нём нет, как нет <c>loca</c> у растрового эмодзи.
        /// </summary>
        private static byte[] NameOnlyFont(string family)
        {
            (ushort Id, string Text)[] names = [(1, family), (2, "Regular"), (4, family)];
            var strings = names.Select(n => Encoding.BigEndianUnicode.GetBytes(n.Text)).ToArray();

            var name = new List<byte>();
            Put16(name, 0);                                // формат
            Put16(name, (ushort)names.Length);
            Put16(name, (ushort)(6 + 12 * names.Length));  // где начинаются строки
            var offset = 0;
            for (var i = 0; i < names.Length; i++)
            {
                Put16(name, 3);       // Windows
                Put16(name, 1);       // Unicode BMP
                Put16(name, 0x0409);  // en-US
                Put16(name, names[i].Id);
                Put16(name, (ushort)strings[i].Length);
                Put16(name, (ushort)offset);
                offset += strings[i].Length;
            }
            foreach (var s in strings) name.AddRange(s);

            var font = new List<byte>();
            Put32(font, 0x00010000);  // TrueType
            Put16(font, 1);           // таблиц
            Put16(font, 16);
            Put16(font, 0);
            Put16(font, 0);
            font.AddRange("name"u8.ToArray());
            Put32(font, 0);           // контрольная сумма — не сверяется
            Put32(font, 12 + 16);     // смещение таблицы
            Put32(font, (uint)name.Count);
            font.AddRange(name);
            return [.. font];
        }

        private static void Put16(List<byte> to, ushort value)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, value);
            to.AddRange(b.ToArray());
        }

        private static void Put32(List<byte> to, uint value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, value);
            to.AddRange(b.ToArray());
        }
    }
}

/// <summary>
/// Тесты, подменяющие кэш шрифтов NPOI, — без соседей: кэш общий на процесс, и параллельный замер
/// автоширины (<c>ImageSharpCompatibilityTests</c>) взял бы подменённый шрифт.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class NpoiFontCacheCollection
{
    public const string Name = "Кэш шрифтов NPOI";
}
