using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>Место съёмки: градусы со знаком (юг и запад — минус), высота в метрах.</summary>
public sealed record ExifGeo(double Lat, double Lon, double? Alt);

/// <summary>Что прочитано из EXIF. Любое поле — <c>null</c>, если тега нет или он повреждён.</summary>
/// <param name="TakenAt">Время съёмки, как его записала камера, — без пояса.</param>
/// <param name="Offset">Пояс съёмки (<c>OffsetTimeOriginal</c>), если камера его записала.</param>
public sealed record ExifFacts(ExifGeo? Geo, DateTime? TakenAt, TimeSpan? Offset)
{
    public static readonly ExifFacts None = new(null, null, null);
}

/// <summary>
/// Ограниченное чтение EXIF из JPEG (work-05, ТЗ WORK-55): геометка и время съёмки — и ничего больше.
///
/// <para><b>Почему своё, а не библиотека.</b> <c>ImageSharp</c> в решении прибит временно и «кодом не
/// используется» — сделать его несущим значило бы потерять право убрать строку. <c>MetadataExtractor</c> —
/// новая зависимость ради семи тегов. Ориентацию читает SkiaSharp (<c>SKCodec.EncodedOrigin</c>), здесь —
/// только GPS и время.</para>
///
/// <para><b>Файл недоверенный</b>, поэтому разбор ограничен:</para>
/// <list type="bullet">
/// <item>один сегмент APP1 с заголовком <c>Exif</c>, не длиннее 64 КБ — длина у маркера JPEG двухбайтная;</item>
/// <item>каждое смещение и каждая длина проверяются против длины сегмента — до чтения;</item>
/// <item>по указателям из IFD0 — ровно один переход в Exif IFD и один в GPS IFD, цепочка «следующих» IFD
///   не читается: зацикленные смещения зациклить нечего;</item>
/// <item>ничего не выделяется по размеру, записанному в файле: строки — не длиннее 32 байт;</item>
/// <item>повреждённый тег даёт <c>null</c>, а не отказ фото — фото без геометки остаётся фото.</item>
/// </list>
/// </summary>
public static class ExifReader
{
    /// <summary>Предел сегмента: длина маркера JPEG двухбайтная, больше не бывает.</summary>
    public const int MaxSegment = 64 * 1024;

    private const ushort TagExifIfd = 0x8769;
    private const ushort TagGpsIfd = 0x8825;
    private const ushort TagDateTimeOriginal = 0x9003;
    private const ushort TagOffsetTimeOriginal = 0x9011;
    private const ushort TagLatRef = 0x0001, TagLat = 0x0002, TagLonRef = 0x0003, TagLon = 0x0004;
    private const ushort TagAltRef = 0x0005, TagAlt = 0x0006;

    private const ushort TypeByte = 1, TypeAscii = 2, TypeRational = 5;

    /// <summary>Геометка и время съёмки из JPEG; не JPEG или нет EXIF — <see cref="ExifFacts.None" />.</summary>
    public static ExifFacts Read(ReadOnlySpan<byte> jpeg)
    {
        try
        {
            return FindExifSegment(jpeg) is { } tiff ? ReadTiff(tiff) : ExifFacts.None;
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            // Проверки смещений стоят перед каждым чтением; это — страховка на случай, который они не
            // предусмотрели. Отказывать фото из-за его метаданных нельзя.
            return ExifFacts.None;
        }
    }

    /// <summary>
    /// TIFF-часть первого сегмента APP1 с заголовком <c>Exif\0\0</c>. Обход маркеров — до начала
    /// сжатых данных (SOS): метаданные JPEG лежат только перед ними.
    /// </summary>
    private static ReadOnlySpan<byte> FindExifSegment(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return default;
        var at = 2;
        while (at + 4 <= jpeg.Length)
        {
            if (jpeg[at] != 0xFF) return default;
            var marker = jpeg[at + 1];
            if (marker == 0xFF) { at++; continue; }            // заполнитель перед маркером
            if (marker is 0xDA or 0xD9) return default;           // SOS, EOI — метаданных дальше нет
            if (marker is >= 0xD0 and <= 0xD7 or 0x01) { at += 2; continue; }   // маркеры без длины

            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(at + 2)..]);
            if (length < 2 || at + 2 + length > jpeg.Length) return default;
            var body = jpeg.Slice(at + 4, length - 2);
            if (marker == 0xE1 && body.Length > 6 && body[..6].SequenceEqual("Exif\0\0"u8))
                return body[6..];
            at += 2 + length;
        }
        return default;
    }

    private static ExifFacts ReadTiff(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8 || tiff.Length > MaxSegment) return ExifFacts.None;
        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I') little = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') little = false;
        else return ExifFacts.None;

        var t = new Tiff(tiff, little);
        if (t.U16(2) != 42) return ExifFacts.None;
        var ifd0 = t.U32(4);

        uint? exifIfd = null, gpsIfd = null;
        foreach (var e in t.Entries(ifd0))
        {
            if (e.Tag == TagExifIfd) exifIfd = t.LongValue(e);
            else if (e.Tag == TagGpsIfd) gpsIfd = t.LongValue(e);
        }

        DateTime? takenAt = null;
        TimeSpan? offset = null;
        if (exifIfd is { } ex)
            foreach (var e in t.Entries(ex))
            {
                if (e.Tag == TagDateTimeOriginal) takenAt = ParseDateTime(t.Ascii(e));
                else if (e.Tag == TagOffsetTimeOriginal) offset = ParseOffset(t.Ascii(e));
            }

        return new ExifFacts(gpsIfd is { } gps ? ReadGeo(t, gps) : null, takenAt, offset);
    }

    private static ExifGeo? ReadGeo(Tiff t, uint ifd)
    {
        string? latRef = null, lonRef = null;
        double? lat = null, lon = null, alt = null;
        var below = false;
        foreach (var e in t.Entries(ifd))
            switch (e.Tag)
            {
                case TagLatRef: latRef = t.Ascii(e); break;
                case TagLonRef: lonRef = t.Ascii(e); break;
                case TagLat: lat = t.Degrees(e); break;
                case TagLon: lon = t.Degrees(e); break;
                case TagAltRef: below = t.ByteValue(e) == 1; break;
                case TagAlt: alt = t.Rational(e, 0); break;
            }

        if (lat is not { } la || lon is not { } lo) return null;
        if (latRef is not ("N" or "S") || lonRef is not ("E" or "W")) return null;
        if (la is < 0 or > 90 || lo is < 0 or > 180) return null;
        return new ExifGeo(
            latRef == "S" ? -la : la,
            lonRef == "W" ? -lo : lo,
            alt is { } a ? (below ? -a : a) : null);
    }

    private static DateTime? ParseDateTime(string? text) =>
        text is not null && DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var at)
            ? DateTime.SpecifyKind(at, DateTimeKind.Unspecified)
            : null;

    private static TimeSpan? ParseOffset(string? text)
    {
        // «+03:00» — знак, часы, минуты. Иное (в том числе «   :  » у камер, не знающих пояса) — нет пояса.
        if (text is not { Length: 6 } || text[0] is not ('+' or '-') || text[3] != ':') return null;
        if (!int.TryParse(text.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(text.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ||
            h > 14 || m > 59)
            return null;
        var span = new TimeSpan(h, m, 0);
        return text[0] == '-' ? -span : span;
    }

    private readonly record struct Entry(ushort Tag, ushort Type, uint Count, int ValueAt);

    /// <summary>TIFF-часть сегмента: чтение с проверкой каждого смещения против его длины.</summary>
    private readonly ref struct Tiff(ReadOnlySpan<byte> data, bool little)
    {
        /// <summary>Предел записей одного IFD: в 64 КБ больше 5461 записей по 12 байт не уложить.</summary>
        private const int MaxEntries = MaxSegment / 12;

        private readonly ReadOnlySpan<byte> data = data;

        public ushort U16(int at) => little
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(at, 2));

        public uint U32(int at) => little
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(at, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(at, 4));

        private bool Fits(long at, long length) => at >= 0 && length >= 0 && at + length <= data.Length;

        /// <summary>Записи IFD по смещению; IFD за пределами сегмента — пусто.</summary>
        public List<Entry> Entries(uint offset)
        {
            var entries = new List<Entry>();
            if (!Fits(offset, 2)) return entries;
            var count = U16((int)offset);
            if (count > MaxEntries || !Fits(offset + 2, count * 12L)) return entries;
            for (var i = 0; i < count; i++)
            {
                var at = (int)offset + 2 + i * 12;
                entries.Add(new Entry(U16(at), U16(at + 2), U32(at + 4), at + 8));
            }
            return entries;
        }

        /// <summary>Где лежат байты значения: в самой записи (до 4 байт) или по смещению. Не влезает — null.</summary>
        private int? ValueAt(Entry e, int size)
        {
            var total = (long)e.Count * size;
            if (total <= 4) return e.ValueAt;
            var at = U32(e.ValueAt);
            return Fits(at, total) ? (int)at : null;
        }

        /// <summary>Указатель на IFD — LONG или, у некоторых камер, SHORT.</summary>
        public uint? LongValue(Entry e) => e.Count != 1 ? null : e.Type switch
        {
            3 => U16(e.ValueAt),
            4 => U32(e.ValueAt),
            _ => null,
        };

        public byte? ByteValue(Entry e) => e.Type == TypeByte && e.Count >= 1 ? data[e.ValueAt] : null;

        /// <summary>Строка ASCII до первого нуля; длиннее 32 байт — не наш тег.</summary>
        public string? Ascii(Entry e)
        {
            if (e.Type != TypeAscii || e.Count is 0 or > 32 || ValueAt(e, 1) is not { } at) return null;
            var bytes = data.Slice(at, (int)e.Count);
            var end = bytes.IndexOf((byte)0);
            return Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]).Trim();
        }

        /// <summary>Рациональное число под номером <paramref name="index" />; знаменатель 0 — null.</summary>
        public double? Rational(Entry e, int index)
        {
            if (e.Type != TypeRational || index >= e.Count || ValueAt(e, 8) is not { } at) return null;
            var num = U32(at + index * 8);
            var den = U32(at + index * 8 + 4);
            return den == 0 ? null : (double)num / den;
        }

        /// <summary>Градусы, минуты, секунды — три рациональных — в доли градуса.</summary>
        public double? Degrees(Entry e)
        {
            if (e.Count != 3) return null;
            if (Rational(e, 0) is not { } d || Rational(e, 1) is not { } m || Rational(e, 2) is not { } s) return null;
            if (m >= 60 || s >= 60) return null;
            return d + m / 60 + s / 3600;
        }
    }
}
