namespace BHS.CRG.Infrastructure.Templates;

/// <summary>
/// Минимальный, независимый от внешних библиотек парсер имени семейства из TTF/OTF/TTC (issue #62) —
/// читает только таблицу "name" (SFNT), достаточную для приоритета шрифтовых ассетов при генерации.
/// Не выполняет полный разбор шрифта (глифы/hinting/etc.) — не нужен для нашей задачи.
/// Формат таблицы "name": https://learn.microsoft.com/typography/opentype/spec/name (публичная спецификация).
/// </summary>
public static class FontFamilyNameReader
{
    private const ushort NameIdFontFamily = 1;
    private const ushort NameIdTypographicFamily = 16;

    /// <summary>Пытается прочитать имя семейства шрифта из байтов файла (TTF/OTF/TTC).
    /// Для TTC берётся первый шрифт коллекции. Возвращает null, если формат не распознан
    /// или подходящая запись имени не найдена — вызывающий код должен толерантно переключиться
    /// на fallback (напр. пользовательское Name), не падать.</summary>
    public static string? TryReadFamilyName(byte[] bytes)
    {
        try
        {
            if (bytes.Length < 12) return null;
            var tag = ReadUInt32(bytes, 0);
            var offsetTableStart = tag == 0x74746366u /* 'ttcf' */
                ? ReadUInt32(bytes, 12) // первое смещение в TTC Header (после version, numFonts)
                : 0u;
            return ReadFamilyNameFromOffsetTable(bytes, (int)offsetTableStart);
        }
        catch
        {
            return null; // повреждённый/непредвиденный формат — толерантно, не критично для фичи
        }
    }

    private static string? ReadFamilyNameFromOffsetTable(byte[] b, int offsetTableStart)
    {
        var numTables = ReadUInt16(b, offsetTableStart + 4);
        var recordsStart = offsetTableStart + 12;

        int nameTableOffset = -1, nameTableLength = 0;
        for (var i = 0; i < numTables; i++)
        {
            var rec = recordsStart + i * 16;
            var tag = ReadUInt32(b, rec);
            if (tag == 0x6E616D65u) // 'name'
            {
                nameTableOffset = (int)ReadUInt32(b, rec + 8);
                nameTableLength = (int)ReadUInt32(b, rec + 12);
                break;
            }
        }
        if (nameTableOffset < 0) return null;

        return ParseNameTable(b, nameTableOffset, nameTableLength);
    }

    private static string? ParseNameTable(byte[] b, int tableStart, int tableLength)
    {
        var count = ReadUInt16(b, tableStart + 2);
        var stringOffset = ReadUInt16(b, tableStart + 4);
        var recordsStart = tableStart + 6;

        string? best = null;
        var bestRank = -1; // выше — приоритетнее

        for (var i = 0; i < count; i++)
        {
            var rec = recordsStart + i * 12;
            if (rec + 12 > tableStart + tableLength) break;
            var platformId = ReadUInt16(b, rec);
            var encodingId = ReadUInt16(b, rec + 2);
            var nameId = ReadUInt16(b, rec + 6);
            var length = ReadUInt16(b, rec + 8);
            var offset = ReadUInt16(b, rec + 10);

            if (nameId != NameIdFontFamily && nameId != NameIdTypographicFamily) continue;

            var strStart = tableStart + stringOffset + offset;
            if (strStart < 0 || strStart + length > b.Length) continue;

            // platformId 3 (Windows)/0 (Unicode) — UTF-16BE; platformId 1 (Mac) — обычно MacRoman (редко
            // нужен современным шрифтам, пропускаем ради простоты — есть UTF-16BE запись почти всегда).
            string text;
            if (platformId is 3 or 0)
                text = System.Text.Encoding.BigEndianUnicode.GetString(b, strStart, length);
            else
                continue;

            // Приоритет: Typographic Family (16) > Font Family (1); Windows Unicode BMP (3,1) предпочтительнее.
            var rank = (nameId == NameIdTypographicFamily ? 10 : 0) + (platformId == 3 && encodingId == 1 ? 1 : 0);
            if (rank > bestRank && !string.IsNullOrWhiteSpace(text))
            {
                best = text;
                bestRank = rank;
            }
        }
        return best;
    }

    private static ushort ReadUInt16(byte[] b, int offset) =>
        (ushort)((b[offset] << 8) | b[offset + 1]);

    private static uint ReadUInt32(byte[] b, int offset) =>
        ((uint)b[offset] << 24) | ((uint)b[offset + 1] << 16) | ((uint)b[offset + 2] << 8) | b[offset + 3];
}
