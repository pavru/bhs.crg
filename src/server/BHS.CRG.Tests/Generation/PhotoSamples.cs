using System.Buffers.Binary;
using System.Text;
using SkiaSharp;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Сгенерированные снимки для тестов подготовки фото (work-05). Настоящих фото здесь нет и быть не должно:
/// картинка рисуется, EXIF собирается байтами, координаты вымышленные.
/// </summary>
internal static class PhotoSamples
{
    /// <summary>Вымышленная точка: 55°45′ с. ш., 37°37′ в. д., 150 м.</summary>
    public static readonly (double Lat, double Lon, double Alt) Place = (55.75, 37.6166666667, 150);

    /// <summary>
    /// JPEG <paramref name="width" />×<paramref name="height" />: белый, с красным левым верхним углом —
    /// по нему видно, куда повернулась картинка.
    /// </summary>
    public static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            using var red = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, width / 4f, height / 4f, red);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    /// <summary>JPEG с сегментом APP1 сразу после SOI — там, где его пишут камеры.</summary>
    public static byte[] WithExif(byte[] jpeg, byte[] tiff)
    {
        var length = 2 + 6 + tiff.Length;
        var segment = new byte[4 + 6 + tiff.Length];
        segment[0] = 0xFF;
        segment[1] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2), (ushort)length);
        "Exif\0\0"u8.CopyTo(segment.AsSpan(4));
        tiff.CopyTo(segment, 10);
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }

    /// <summary>
    /// TIFF-часть EXIF: ориентация в IFD0, время съёмки в Exif IFD, координаты в GPS IFD. Каждое — по
    /// желанию.
    /// </summary>
    public static byte[] Exif(
        ushort? orientation = null, (double Lat, double Lon, double Alt)? geo = null,
        string? takenAt = null, string? offset = null, bool bigEndian = false)
    {
        var tiff = new TiffBuilder(bigEndian);
        var ifd0 = tiff.Ifd();
        if (orientation is { } o) ifd0.Short(0x0112, o);

        if (takenAt is not null || offset is not null)
        {
            var exif = tiff.Ifd();
            if (takenAt is not null) exif.Ascii(0x9003, takenAt);
            if (offset is not null) exif.Ascii(0x9011, offset);
            ifd0.Pointer(0x8769, exif);
        }

        if (geo is { } g)
        {
            var gps = tiff.Ifd();
            gps.Ascii(0x0001, g.Lat >= 0 ? "N" : "S");
            gps.Rationals(0x0002, Dms(Math.Abs(g.Lat)));
            gps.Ascii(0x0003, g.Lon >= 0 ? "E" : "W");
            gps.Rationals(0x0004, Dms(Math.Abs(g.Lon)));
            gps.Byte(0x0005, g.Alt >= 0 ? (byte)0 : (byte)1);
            gps.Rationals(0x0006, [((uint)Math.Round(Math.Abs(g.Alt) * 100), 100u)]);
            ifd0.Pointer(0x8825, gps);
        }

        return tiff.Build();
    }

    /// <summary>Градусы, минуты и секунды (с сотыми) — так их пишут камеры.</summary>
    private static (uint, uint)[] Dms(double value)
    {
        var d = (uint)Math.Floor(value);
        var minutes = (value - d) * 60;
        var m = (uint)Math.Floor(minutes);
        var s = (uint)Math.Round((minutes - m) * 60 * 100);
        return [(d, 1), (m, 1), (s, 100)];
    }

    /// <summary>Маркеры JPEG до начала сжатых данных — чтобы проверить, что APP1 в результате нет.</summary>
    public static List<byte> Markers(byte[] jpeg)
    {
        var markers = new List<byte>();
        var at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF)
        {
            var marker = jpeg[at + 1];
            markers.Add(marker);
            if (marker == 0xDA) break;
            at += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(at + 2));
        }
        return markers;
    }

    /// <summary>
    /// PNG с заголовком <paramref name="width" />×<paramref name="height" /> и мусором вместо пикселей —
    /// «бомба разжатия»: файл на <paramref name="bytes" />, а развернуть его значило бы выделить гигабайты.
    /// Декодировать его тестам не нужно — служба обязана отказать по заголовку.
    /// </summary>
    public static byte[] PngBomb(int width, int height, int bytes)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;   // глубина
        ihdr[9] = 2;   // RGB

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", new byte[Math.Max(0, bytes - 8 - 25 - 12 - 12)]);
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typed));
        png.Write(number);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }

    /// <summary>
    /// Сборщик TIFF: IFD подряд от смещения 8, длинные значения — после всех IFD. Смещения считаются при
    /// сборке, поэтому указатель на IFD можно назвать раньше, чем тот заполнен.
    /// </summary>
    public sealed class TiffBuilder(bool bigEndian)
    {
        private readonly List<IfdBuilder> ifds = [];

        public IfdBuilder Ifd()
        {
            var ifd = new IfdBuilder(this);
            ifds.Add(ifd);
            return ifd;
        }

        internal void U16(Span<byte> at, ushort v)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(at, v);
            else BinaryPrimitives.WriteUInt16LittleEndian(at, v);
        }

        internal void U32(Span<byte> at, uint v)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(at, v);
            else BinaryPrimitives.WriteUInt32LittleEndian(at, v);
        }

        public byte[] Build()
        {
            var offsets = new Dictionary<IfdBuilder, uint>();
            uint at = 8;
            foreach (var ifd in ifds)
            {
                offsets[ifd] = at;
                at += (uint)(2 + ifd.Entries.Count * 12 + 4);
            }

            var data = new List<byte>();
            var result = new byte[at];
            result[0] = result[1] = bigEndian ? (byte)'M' : (byte)'I';
            U16(result.AsSpan(2), 42);
            U32(result.AsSpan(4), 8);

            foreach (var ifd in ifds)
            {
                var start = (int)offsets[ifd];
                U16(result.AsSpan(start), (ushort)ifd.Entries.Count);
                for (var i = 0; i < ifd.Entries.Count; i++)
                {
                    var (tag, type, count, value, pointer) = ifd.Entries[i];
                    var e = result.AsSpan(start + 2 + i * 12, 12);
                    U16(e, tag);
                    U16(e[2..], type);
                    U32(e[4..], count);
                    if (pointer is not null) U32(e[8..], offsets[pointer]);
                    else if (value.Length <= 4) value.CopyTo(e[8..]);
                    else
                    {
                        U32(e[8..], at + (uint)data.Count);
                        data.AddRange(value);
                    }
                }
            }
            return [.. result, .. data];
        }
    }

    public sealed class IfdBuilder(TiffBuilder tiff)
    {
        internal List<(ushort Tag, ushort Type, uint Count, byte[] Value, IfdBuilder? Pointer)> Entries { get; } = [];

        public void Short(ushort tag, ushort value)
        {
            var bytes = new byte[2];
            tiff.U16(bytes, value);
            Entries.Add((tag, 3, 1, bytes, null));
        }

        public void Byte(ushort tag, byte value) => Entries.Add((tag, 1, 1, [value], null));

        public void Ascii(ushort tag, string value) =>
            Entries.Add((tag, 2, (uint)value.Length + 1, [.. Encoding.ASCII.GetBytes(value), 0], null));

        public void Rationals(ushort tag, (uint Num, uint Den)[] values)
        {
            var bytes = new byte[values.Length * 8];
            for (var i = 0; i < values.Length; i++)
            {
                tiff.U32(bytes.AsSpan(i * 8), values[i].Num);
                tiff.U32(bytes.AsSpan(i * 8 + 4), values[i].Den);
            }
            Entries.Add((tag, 5, (uint)values.Length, bytes, null));
        }

        public void Pointer(ushort tag, IfdBuilder target) => Entries.Add((tag, 4, 1, [], target));

        /// <summary>Запись с произвольными сырыми полями — для повреждённых EXIF.</summary>
        public void Raw(ushort tag, ushort type, uint count, uint valueOrOffset)
        {
            var bytes = new byte[4];
            tiff.U32(bytes, valueOrOffset);
            Entries.Add((tag, type, count, bytes, null));
        }
    }
}
