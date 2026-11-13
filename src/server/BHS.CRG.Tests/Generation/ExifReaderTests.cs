using System.Buffers.Binary;
using BHS.CRG.Infrastructure.Generation;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Ограниченное чтение EXIF (work-05, ТЗ WORK-55). Файл недоверенный: обрезанный, зацикленный или
/// просто враньё в смещениях обязан дать «геометки нет», а не исключение и не чтение за пределами.
/// </summary>
public class ExifReaderTests
{
    private static ExifFacts Read(byte[] tiff) => ExifReader.Read(PhotoSamples.WithExif(PhotoSamples.Jpeg(64, 48), tiff));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Координаты_и_время_читаются_в_обоих_порядках_байт(bool bigEndian)
    {
        var facts = Read(PhotoSamples.Exif(geo: PhotoSamples.Place, takenAt: "2026:11:13 10:20:30",
            offset: "+03:00", bigEndian: bigEndian));

        Assert.NotNull(facts.Geo);
        Assert.Equal(PhotoSamples.Place.Lat, facts.Geo.Lat, 4);
        Assert.Equal(PhotoSamples.Place.Lon, facts.Geo.Lon, 4);
        Assert.Equal(PhotoSamples.Place.Alt, facts.Geo.Alt!.Value, 2);
        Assert.Equal(new DateTime(2026, 11, 13, 10, 20, 30), facts.TakenAt);
        Assert.Equal(TimeSpan.FromHours(3), facts.Offset);
    }

    [Fact]
    public void Юг_и_запад_со_знаком_минус_высота_ниже_уровня_моря_тоже()
    {
        var facts = Read(PhotoSamples.Exif(geo: (-33.5, -70.25, -12)));

        Assert.NotNull(facts.Geo);
        Assert.Equal(-33.5, facts.Geo.Lat, 4);
        Assert.Equal(-70.25, facts.Geo.Lon, 4);
        Assert.Equal(-12, facts.Geo.Alt!.Value, 2);
    }

    [Fact]
    public void Без_EXIF_ничего()
    {
        Assert.Same(ExifFacts.None, ExifReader.Read(PhotoSamples.Jpeg(64, 48)));
        Assert.Same(ExifFacts.None, ExifReader.Read([]));
        Assert.Same(ExifFacts.None, ExifReader.Read("not a jpeg at all"u8));
    }

    [Fact]
    public void Время_без_пояса_приходит_без_пояса()
    {
        var facts = Read(PhotoSamples.Exif(takenAt: "2026:11:13 10:20:30"));

        Assert.Equal(new DateTime(2026, 11, 13, 10, 20, 30), facts.TakenAt);
        Assert.Null(facts.Offset);
        Assert.Null(facts.Geo);
    }

    /// <summary>
    /// Обрезанный EXIF: сегмент заявляет полную длину, а TIFF-часть кончается посреди GPS IFD. Длина
    /// маркера тогда выходит за файл — сегмент не читается вовсе.
    /// </summary>
    [Fact]
    public void Обрезанный_EXIF_даёт_пустоту_без_исключения()
    {
        var tiff = PhotoSamples.Exif(geo: PhotoSamples.Place, takenAt: "2026:11:13 10:20:30");
        var jpeg = PhotoSamples.WithExif(PhotoSamples.Jpeg(64, 48), tiff);

        for (var cut = 3; cut < 10 + tiff.Length; cut += 7)
        {
            var facts = ExifReader.Read(jpeg.AsSpan(0, 2 + cut));
            Assert.Null(facts.Geo);
        }
    }

    /// <summary>
    /// TIFF-часть короче, чем указывают её смещения: длина сегмента честная, а IFD и значения — за его
    /// концом. Каждое смещение проверяется против длины сегмента.
    /// </summary>
    [Fact]
    public void Смещения_за_концом_сегмента_дают_пустоту()
    {
        var tiff = PhotoSamples.Exif(geo: PhotoSamples.Place, takenAt: "2026:11:13 10:20:30");

        // Последние 8 байт — высота; без неё геометка остаётся, просто без высоты.
        for (var length = 8; length < tiff.Length - 8; length += 5)
            Assert.Null(Read(tiff[..length]).Geo);
        Assert.Null(Read(tiff[..^4]).Geo?.Alt);
    }

    /// <summary>
    /// Зацикленный EXIF: указатель на Exif IFD ведёт обратно в IFD0, указатель на GPS IFD — сам на себя,
    /// «следующий IFD» — в начало. Разбор идёт ровно на один переход, зациклить его нечем.
    /// </summary>
    [Fact]
    public void Зацикленные_смещения_дают_пустоту_без_исключения()
    {
        var tiff = new PhotoSamples.TiffBuilder(bigEndian: false);
        var ifd0 = tiff.Ifd();
        ifd0.Raw(0x8769, 4, 1, 8);    // Exif IFD → IFD0
        ifd0.Raw(0x8825, 4, 1, 8);    // GPS IFD → IFD0
        var bytes = tiff.Build();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), 8);   // следующий IFD → IFD0

        var facts = Read(bytes);

        Assert.Null(facts.Geo);
        Assert.Null(facts.TakenAt);
    }

    [Fact]
    public void Нулевой_знаменатель_и_чужие_типы_дают_пустоту()
    {
        var tiff = new PhotoSamples.TiffBuilder(bigEndian: false);
        var ifd0 = tiff.Ifd();
        var gps = tiff.Ifd();
        ifd0.Pointer(0x8825, gps);
        gps.Ascii(0x0001, "N");
        gps.Rationals(0x0002, [(55, 0), (45, 1), (0, 1)]);   // знаменатель 0
        gps.Ascii(0x0003, "E");
        gps.Raw(0x0004, 3, 3, 0xFFFF_FFF0);                     // SHORT вместо RATIONAL, смещение в никуда

        Assert.Null(Read(tiff.Build()).Geo);
    }

    [Fact]
    public void Координаты_вне_диапазона_дают_пустоту()
    {
        Assert.Null(Read(PhotoSamples.Exif(geo: (95, 37, 0))).Geo);
    }

    [Fact]
    public void Огромное_число_записей_не_читается()
    {
        var bytes = PhotoSamples.Exif(geo: PhotoSamples.Place);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 0xFFFF);   // IFD0: 65535 записей

        Assert.Null(Read(bytes).Geo);
    }
}
