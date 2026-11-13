using SkiaSharp;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>Пределы подготовки фото — те, что назвал модуль.</summary>
public sealed record PhotoPreparerLimits(long MaxBytes, int MaxSide, int PreviewSide, long MaxPixels);

/// <summary>Итог подготовки фото: <see cref="PhotoPrepared" /> или <see cref="PhotoRefused" />.</summary>
public abstract record PhotoPreparation;

/// <summary>JPEG и превью, повёрнутые по EXIF и без метаданных снимка; геометка и время — данными.</summary>
public sealed record PhotoPrepared(
    byte[] Image, byte[] Preview, int Width, int Height, ExifGeo? Geo, DateTime? TakenAt, TimeSpan? Offset)
    : PhotoPreparation;

/// <summary>Отказ с причиной.</summary>
public sealed record PhotoRefused(PhotoRefusalReason Reason, string Text) : PhotoPreparation;

public enum PhotoRefusalReason { TooLarge, TooManyPixels, NotAnImage }

/// <summary>
/// Подготовка фото для модулей (work-05, ТЗ WORK-33, WORK-55): чтение с пределом, проверка площади по
/// заголовку, поворот по EXIF, уменьшение и превью, JPEG всегда.
///
/// <para><b>Чем отличается от <see cref="ImageDownscaler" />.</b> Тот служит вложениям: картинку в
/// пределах порога не трогает вовсе, прозрачную оставляет PNG и ориентацию EXIF не читает. Для фото это
/// не годится: снимок с телефона лёг бы набок, а оригинал в пределах порога хранился бы со всеми
/// метаданными — моделью и серийным номером устройства. Поэтому здесь перекодирование ВСЕГДА. Порог
/// длинной стороны и интерполяция — общие (<see cref="ImageDownscaler.Fit" />,
/// <see cref="ImageDownscaler.Sampling" />): живут в одном месте.</para>
///
/// <para><b>Порядок проверок — от дешёвой к дорогой.</b> Байты — до чтения целиком, пиксели — по
/// заголовку до декодирования. Только потом декодирование. Так ни файл на 30 МБ, ни PNG на 2 МБ размером
/// 20000×20000 не доходят до памяти, которой им не хватит.</para>
/// </summary>
public static class PhotoPreparer
{
    /// <summary>Подготовить фото из потока. Поток читается не дальше <c>MaxBytes + 1</c> байта.</summary>
    public static async Task<PhotoPreparation> PrepareAsync(
        Stream content, PhotoPreparerLimits limits, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxSide);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.PreviewSide);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.MaxPixels);

        var bytes = await ReadAtMostAsync(content, limits.MaxBytes, ct);
        if (bytes is null)
            return new PhotoRefused(PhotoRefusalReason.TooLarge, $"Файл больше {Size(limits.MaxBytes)}.");
        return Prepare(bytes, limits);
    }

    /// <summary>Подготовить уже прочитанное фото — после проверки предела в байтах.</summary>
    private static PhotoPreparation Prepare(byte[] bytes, PhotoPreparerLimits limits)
    {
        using var codec = CreateCodec(bytes);
        if (codec is null) return NotAnImage();

        // Площадь — по заголовку: SKCodec пиксели не декодирует. Текст тот же, что у вложений ядра.
        if (ImageDownscaler.PixelCountExceeded(bytes, limits.MaxPixels) is { } tooMany)
            return new PhotoRefused(PhotoRefusalReason.TooManyPixels, tooMany);

        var origin = codec.EncodedOrigin;
        SKBitmap? decoded;
        try { decoded = SKBitmap.Decode(codec); }
        catch (Exception) { decoded = null; }
        if (decoded is null) return NotAnImage();

        using (decoded)
        {
            // Уменьшение — до поворота: длинная сторона от поворота не меняется, а вертеть меньшую
            // картинку дешевле.
            // SkiaSharp, не сумевший уменьшить картинку, — та же картинка, которую не прочли: отказ
            // значением, а не «внутренняя ошибка» на загрузке.
            if (Resize(decoded, limits.MaxSide) is not { } resized) return NotAnImage();
            using var image = Orient(resized, origin, out var width, out var height);
            using var preview = Resize(image, limits.PreviewSide);
            if (preview is null) return NotAnImage();
            var facts = ExifReader.Read(bytes);
            return new PhotoPrepared(Jpeg(image), Jpeg(preview), width, height, facts.Geo, facts.TakenAt, facts.Offset);
        }
    }

    private static PhotoRefused NotAnImage() =>
        new(PhotoRefusalReason.NotAnImage, "Файл не распознан как изображение.");

    /// <summary>
    /// Поток целиком, если в нём не больше <paramref name="maxBytes" /> байт; иначе <c>null</c>, и дальше
    /// <c>maxBytes + 1</c> байта поток не читается — файл на 30 МБ при пределе 15 не дочитывается.
    /// </summary>
    private static async Task<byte[]?> ReadAtMostAsync(Stream content, long maxBytes, CancellationToken ct)
    {
        if (content.CanSeek && content.Length - content.Position > maxBytes) return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            var want = (int)Math.Min(chunk.Length, maxBytes + 1 - total);
            var read = await content.ReadAsync(chunk.AsMemory(0, want), ct);
            if (read == 0) return buffer.ToArray();
            total += read;
            if (total > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
    }

    private static SKCodec? CreateCodec(byte[] bytes)
    {
        // Нераспознанные байты SKCodec отдаёт null, но на части повреждённых файлов — бросает.
        try { return SKCodec.Create(new MemoryStream(bytes)); }
        catch (Exception) { return null; }
    }

    /// <summary>Уменьшенная копия или та же картинка копией, если она уже в пределах; не вышло — null.</summary>
    private static SKBitmap? Resize(SKBitmap source, int maxSide)
    {
        var (width, height) = ImageDownscaler.Fit(source.Width, source.Height, maxSide);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, source.ColorSpace);
        return width == source.Width && height == source.Height
            ? source.Copy()
            : source.Resize(info, ImageDownscaler.Sampling);
    }

    /// <summary>
    /// Картинка, повёрнутая по отметке EXIF, — пиксель в пиксель: каждая ориентация — перестановка
    /// осей, без интерполяции. Заодно непрозрачная: PNG с прозрачностью ложится на белое, а не на
    /// чёрное, которым JPEG заменил бы прозрачные пиксели. Исходную освобождает.
    /// </summary>
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin, out int width, out int height)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        width = swap ? source.Height : source.Width;
        height = swap ? source.Width : source.Height;

        float w = width, h = height;
        // x' = ScaleX·x + SkewX·y + TransX, y' = SkewY·x + ScaleY·y + TransY — в координатах результата.
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),      // 2: зеркально
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),  // 3: на 180°
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),    // 4: вверх ногами зеркально
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),        // 5: транспонирование
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),      // 6: на 90° по часовой
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),  // 7: поперечное отражение
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),    // 8: на 90° против часовой
            _ => SKMatrix.Identity,
        };

        using (source)
        {
            var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque, source.ColorSpace));
            using var canvas = new SKCanvas(result);
            canvas.Clear(SKColors.White);
            canvas.SetMatrix(matrix);
            canvas.DrawBitmap(source, 0, 0);
            return result;
        }
    }

    /// <summary>
    /// JPEG всегда: прозрачности у фото не бывает, а перекодирование нужно ради поворота и снятия
    /// метаданных — кодировщик SkiaSharp EXIF не пишет.
    /// </summary>
    private static byte[] Jpeg(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, ImageDownscaler.JpegQuality);
        return data.ToArray();
    }

    /// <summary>«15 МБ», «1,5 МБ», «500 КБ» — предел так, как его прочтёт человек.</summary>
    public static string Size(long bytes)
    {
        const double kb = 1024, mb = kb * 1024;
        var culture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        return bytes >= mb
            ? (bytes / mb).ToString("0.#", culture) + " МБ"
            : bytes >= kb
                ? (bytes / kb).ToString("0.#", culture) + " КБ"
                : bytes + " Б";
    }
}
