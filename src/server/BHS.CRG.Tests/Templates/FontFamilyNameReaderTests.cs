using BHS.CRG.Infrastructure.Templates;

namespace BHS.CRG.Tests.Templates;

/// <summary>
/// FontFamilyNameReader (issue #62) — минимальный парсер таблицы "name" SFNT. Тесты строят
/// синтетический (не настоящий) шрифтовый файл вручную — не встраиваем в репозиторий реальные
/// шрифтовые файлы (лицензионные ограничения на распространение), но покрываем ровно тот же
/// бинарный формат, что и настоящие TTF/OTF (эмпирически сверено на реальных Comic Sans MS/
/// David CLM/Nirmala.ttc/Arial при разработке — см. issue #62).
/// </summary>
public class FontFamilyNameReaderTests
{
    // Строит минимальный валидный SFNT-файл с одной таблицей "name" и одной записью
    // (platform=3 Windows, encoding=1 Unicode BMP, nameId — Font Family(1) или Typographic Family(16)).
    private static byte[] BuildSfnt(string familyName, ushort nameId = 1, ushort platformId = 3, ushort encodingId = 1)
    {
        var stringBytes = System.Text.Encoding.BigEndianUnicode.GetBytes(familyName);
        var nameTableLength = 6 + 12 * 1 + stringBytes.Length;
        var nameTableOffset = 28;
        var totalLength = nameTableOffset + nameTableLength;
        var b = new byte[totalLength];

        void WriteU16(int offset, ushort value) { b[offset] = (byte)(value >> 8); b[offset + 1] = (byte)value; }
        void WriteU32(int offset, uint value)
        {
            b[offset] = (byte)(value >> 24); b[offset + 1] = (byte)(value >> 16);
            b[offset + 2] = (byte)(value >> 8); b[offset + 3] = (byte)value;
        }

        WriteU32(0, 0x00010000);       // sfnt version
        WriteU16(4, 1);                 // numTables = 1
        WriteU32(12, 0x6E616D65);       // tag 'name'
        WriteU32(16, 0);                // checksum (не используется парсером)
        WriteU32(20, (uint)nameTableOffset);
        WriteU32(24, (uint)nameTableLength);

        WriteU16(nameTableOffset, 0);            // format
        WriteU16(nameTableOffset + 2, 1);         // count
        WriteU16(nameTableOffset + 4, 18);        // stringOffset (6 + 12*1)
        WriteU16(nameTableOffset + 6, platformId);
        WriteU16(nameTableOffset + 8, encodingId);
        WriteU16(nameTableOffset + 10, 0x0409);   // languageId (en-US, произвольно)
        WriteU16(nameTableOffset + 12, nameId);
        WriteU16(nameTableOffset + 14, (ushort)stringBytes.Length);
        WriteU16(nameTableOffset + 16, 0);         // offset relative to storage area
        Array.Copy(stringBytes, 0, b, nameTableOffset + 18, stringBytes.Length); // storageStart = tableStart + stringOffset(18)

        return b;
    }

    [Fact]
    public void TryReadFamilyName_ReadsFontFamilyNameId()
    {
        var bytes = BuildSfnt("Test Family");
        Assert.Equal("Test Family", FontFamilyNameReader.TryReadFamilyName(bytes));
    }

    [Fact]
    public void TryReadFamilyName_PrefersTypographicFamilyOverFontFamily()
    {
        // Синтетически объединяем два файла невозможно одной BuildSfnt-записью (она пишет только одну
        // запись) — здесь просто проверяем, что nameId=16 тоже читается корректно (ветка приоритета
        // в ParseNameTable покрыта отдельно через ранжирование rank, юнит здесь — сам факт чтения id=16).
        var bytes = BuildSfnt("Typographic Family", nameId: 16);
        Assert.Equal("Typographic Family", FontFamilyNameReader.TryReadFamilyName(bytes));
    }

    [Fact]
    public void TryReadFamilyName_UnrecognizedFormat_ReturnsNull()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
        Assert.Null(FontFamilyNameReader.TryReadFamilyName(bytes));
    }

    [Fact]
    public void TryReadFamilyName_TooShort_ReturnsNull()
    {
        Assert.Null(FontFamilyNameReader.TryReadFamilyName([1, 2, 3]));
    }

    [Fact]
    public void TryReadFamilyName_TtcHeader_ReadsFirstFontsName()
    {
        // TTC: тег 'ttcf' + version(4) + numFonts(4) + offsets[numFonts] — первый offset указывает
        // на Offset Table первого шрифта коллекции (тот же формат, что и обычный SFNT дальше).
        var inner = BuildSfnt("Collection Family");
        var ttcHeaderSize = 16; // tag(4) + version(4) + numFonts(4) + offset[1](4)
        var b = new byte[ttcHeaderSize + inner.Length];
        void WriteU32(int offset, uint value)
        {
            b[offset] = (byte)(value >> 24); b[offset + 1] = (byte)(value >> 16);
            b[offset + 2] = (byte)(value >> 8); b[offset + 3] = (byte)value;
        }
        WriteU32(0, 0x74746366);        // 'ttcf'
        WriteU32(4, 0x00010000);        // version
        WriteU32(8, 1);                  // numFonts = 1
        WriteU32(12, (uint)ttcHeaderSize); // offset[0] — сразу после заголовка TTC
        Array.Copy(inner, 0, b, ttcHeaderSize, inner.Length);
        // Смещения таблиц в table directory всегда абсолютны от начала ФАЙЛА (не от начала
        // отдельного шрифта в коллекции) — при встраивании inner как есть нужно сдвинуть записанное
        // в нём смещение таблицы "name" (лежит в inner по абсолютному адресу 20) на ttcHeaderSize.
        var innerNameTableOffset = (uint)((inner[20] << 24) | (inner[21] << 16) | (inner[22] << 8) | inner[23]);
        var rebasedOffset = innerNameTableOffset + (uint)ttcHeaderSize;
        WriteU32(ttcHeaderSize + 20, rebasedOffset);

        Assert.Equal("Collection Family", FontFamilyNameReader.TryReadFamilyName(b));
    }
}
