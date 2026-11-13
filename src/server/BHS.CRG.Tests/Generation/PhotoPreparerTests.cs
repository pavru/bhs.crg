using System.Text;
using BHS.CRG.Infrastructure.Generation;
using SkiaSharp;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Подготовка фото для модулей (work-05, ТЗ WORK-33, WORK-55). Каждый тест — ровно то, чем ломается
/// фото без службы: оригинал из браузера в хранилище, снимок набок, файл на 30 МБ в памяти, бомба
/// разжатия, метаданные устройства в сохранённом файле.
/// </summary>
public class PhotoPreparerTests
{
    private const long Mb = 1024 * 1024;

    private static readonly PhotoPreparerLimits Limits =
        new(MaxBytes: 15 * Mb, MaxSide: 2400, PreviewSide: 480, MaxPixels: 64_000_000);

    private static Task<PhotoPreparation> Prepare(byte[] bytes, PhotoPreparerLimits? limits = null) =>
        PhotoPreparer.PrepareAsync(new MemoryStream(bytes), limits ?? Limits);

    private static SKBitmap Decode(byte[] jpeg) =>
        SKBitmap.Decode(jpeg) ?? throw new InvalidOperationException("Результат не декодируется.");

    /// <summary>
    /// Файл больше предела отвергается, не дочитанным: из потока взято не больше <c>MaxBytes + 1</c>
    /// байта. Поток без длины — как у загрузки, которая ещё идёт: заранее размер не узнать.
    /// </summary>
    [Fact]
    public async Task Файл_больше_лимита_отвергается_не_дочитанным()
    {
        var stream = new EndlessStream(30 * Mb);

        var result = await PhotoPreparer.PrepareAsync(stream, Limits);

        var refused = Assert.IsType<PhotoRefused>(result);
        Assert.Equal(PhotoRefusalReason.TooLarge, refused.Reason);
        Assert.Equal("Файл больше 15 МБ.", refused.Text);
        Assert.True(stream.Taken <= Limits.MaxBytes + 1,
            $"Прочитано {stream.Taken} байт при пределе {Limits.MaxBytes}: файл дочитывался в память.");
    }

    /// <summary>Поток с длиной отвергается до чтения вовсе.</summary>
    [Fact]
    public async Task Файл_с_известной_длиной_отвергается_до_чтения()
    {
        var result = await Prepare(new byte[2 * Mb], Limits with { MaxBytes = Mb + Mb / 2 });

        Assert.Equal("Файл больше 1,5 МБ.", Assert.IsType<PhotoRefused>(result).Text);
    }

    /// <summary>
    /// PNG на 2 МБ размером 20000×20000 развернулся бы в 1,6 ГБ памяти. Отказ — по заголовку: пиксели
    /// в файле — мусор, и декодирование, начнись оно, отказало бы иначе.
    /// </summary>
    [Fact]
    public async Task Бомба_разжатия_отвергается_по_заголовку()
    {
        var bomb = PhotoSamples.PngBomb(20000, 20000, (int)(2 * Mb));

        var refused = Assert.IsType<PhotoRefused>(await Prepare(bomb));

        Assert.Equal(PhotoRefusalReason.TooManyPixels, refused.Reason);
        Assert.Contains("20000×20000", refused.Text);
    }

    [Fact]
    public async Task Не_изображение_отказ_а_не_пустой_результат()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF");

        var refused = Assert.IsType<PhotoRefused>(await Prepare(pdf));

        Assert.Equal(PhotoRefusalReason.NotAnImage, refused.Reason);
        Assert.Equal("Файл не распознан как изображение.", refused.Text);
    }

    [Fact]
    public async Task Повреждённый_JPEG_отказ()
    {
        var jpeg = PhotoSamples.Jpeg(800, 600);

        var refused = Assert.IsType<PhotoRefused>(await Prepare(jpeg[..200]));

        Assert.Equal(PhotoRefusalReason.NotAnImage, refused.Reason);
    }

    /// <summary>Веб-загрузка без сжатия на клиенте: 4000×3000 ложится 2400×1800, превью — 480×360.</summary>
    [Fact]
    public async Task Веб_фото_уменьшается_до_длинной_стороны_и_получает_превью()
    {
        var photo = Assert.IsType<PhotoPrepared>(await Prepare(PhotoSamples.Jpeg(4000, 3000)));

        Assert.Equal((2400, 1800), (photo.Width, photo.Height));
        using var image = Decode(photo.Image);
        Assert.Equal((2400, 1800), (image.Width, image.Height));
        using var preview = Decode(photo.Preview);
        Assert.Equal((480, 360), (preview.Width, preview.Height));
    }

    /// <summary>
    /// Снимок телефона «набок»: 4032×3024 с ориентацией 6 («повернуть на 90° по часовой») ложится книжным
    /// 1800×2400. Красный угол исходного левого верха после поворота — справа сверху.
    /// </summary>
    [Fact]
    public async Task Ориентация_EXIF_применяется()
    {
        var shot = PhotoSamples.WithExif(PhotoSamples.Jpeg(4032, 3024), PhotoSamples.Exif(orientation: 6));

        var photo = Assert.IsType<PhotoPrepared>(await Prepare(shot));

        Assert.Equal((1800, 2400), (photo.Width, photo.Height));
        using var image = Decode(photo.Image);
        Assert.Equal((1800, 2400), (image.Width, image.Height));
        Assert.True(IsRed(image.GetPixel(1800 - 50, 50)), "Правый верхний угол не красный: поворот не тот.");
        Assert.False(IsRed(image.GetPixel(50, 50)), "Левый верхний угол красный: снимок не повернули.");
        // Отметки ориентации в результате нет: показ, применивший бы её второй раз, повернул бы снимок снова.
        using var codec = SKCodec.Create(new MemoryStream(photo.Image));
        Assert.Equal(SKEncodedOrigin.TopLeft, codec.EncodedOrigin);
    }

    [Theory]
    [InlineData(3, 800, 600, 750, 550)]   // на 180°: красный угол — справа снизу
    [InlineData(8, 600, 800, 50, 750)]    // на 90° против часовой: красный — слева снизу
    [InlineData(2, 800, 600, 750, 50)]    // зеркально: красный — справа сверху
    public async Task Прочие_ориентации_EXIF_применяются(ushort orientation, int width, int height, int redX, int redY)
    {
        var shot = PhotoSamples.WithExif(PhotoSamples.Jpeg(800, 600), PhotoSamples.Exif(orientation: orientation));

        var photo = Assert.IsType<PhotoPrepared>(await Prepare(shot));

        Assert.Equal((width, height), (photo.Width, photo.Height));
        using var image = Decode(photo.Image);
        Assert.True(IsRed(image.GetPixel(redX, redY)), $"Ориентация {orientation}: красный угол не на месте.");
    }

    /// <summary>
    /// Снимок с геометкой: координаты и время — полями результата, а в возвращённом файле EXIF нет. Фото в
    /// пределах длинной стороны перекодируется всё равно: иначе метаданные остались бы в файле.
    /// </summary>
    [Fact]
    public async Task Геометка_данными_а_в_файле_EXIF_нет()
    {
        var shot = PhotoSamples.WithExif(PhotoSamples.Jpeg(800, 600),
            PhotoSamples.Exif(geo: PhotoSamples.Place, takenAt: "2026:11:13 10:20:30", offset: "+03:00"));
        Assert.Contains((byte)0xE1, PhotoSamples.Markers(shot));

        var photo = Assert.IsType<PhotoPrepared>(await Prepare(shot));

        Assert.NotNull(photo.Geo);
        Assert.Equal(PhotoSamples.Place.Lat, photo.Geo.Lat, 4);
        Assert.Equal(PhotoSamples.Place.Lon, photo.Geo.Lon, 4);
        Assert.Equal(PhotoSamples.Place.Alt, photo.Geo.Alt!.Value, 2);
        Assert.Equal(new DateTime(2026, 11, 13, 10, 20, 30), photo.TakenAt);
        Assert.Equal(TimeSpan.FromHours(3), photo.Offset);
        Assert.Equal((800, 600), (photo.Width, photo.Height));
        Assert.DoesNotContain((byte)0xE1, PhotoSamples.Markers(photo.Image));
        Assert.DoesNotContain((byte)0xE1, PhotoSamples.Markers(photo.Preview));
        Assert.Same(ExifFacts.None, ExifReader.Read(photo.Image));
    }

    [Fact]
    public async Task Снимок_без_геометки_обрабатывается()
    {
        var photo = Assert.IsType<PhotoPrepared>(await Prepare(PhotoSamples.Jpeg(800, 600)));

        Assert.Null(photo.Geo);
        Assert.Null(photo.TakenAt);
    }

    /// <summary>PNG с прозрачностью ложится на белое: JPEG заменил бы прозрачные пиксели чёрными.</summary>
    [Fact]
    public async Task Прозрачный_PNG_ложится_на_белое()
    {
        using var bitmap = new SKBitmap(100, 100, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.Transparent);
        using var png = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);

        var photo = Assert.IsType<PhotoPrepared>(await Prepare(png.ToArray()));

        using var image = Decode(photo.Image);
        Assert.True(image.GetPixel(50, 50).Red > 240, "Прозрачное стало тёмным.");
    }

    [Fact]
    public void Предел_называется_так_как_его_прочтёт_человек()
    {
        Assert.Equal("15 МБ", PhotoPreparer.Size(15 * Mb));
        Assert.Equal("1,5 МБ", PhotoPreparer.Size(Mb + Mb / 2));
        Assert.Equal("500 КБ", PhotoPreparer.Size(500 * 1024));
    }

    private static bool IsRed(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;

    /// <summary>Поток без длины и без конца в пределах <paramref name="size" />; считает прочитанное.</summary>
    private sealed class EndlessStream(long size) : Stream
    {
        public long Taken { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, size - Taken);
            Array.Clear(buffer, offset, n);
            Taken += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
