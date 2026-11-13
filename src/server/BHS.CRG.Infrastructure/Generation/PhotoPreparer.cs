using BHS.CRG.Infrastructure.Common;
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
            return new PhotoRefused(PhotoRefusalReason.TooLarge, $"Файл больше {ByteSizeText.Format(limits.MaxBytes)}.");
        return Prepare(bytes, limits);
    }

    /// <summary>Подготовить уже прочитанное фото — после проверки предела в байтах.</summary>
    private static PhotoPreparation Prepare(byte[] bytes, PhotoPreparerLimits limits)
    {
        using var codec = CreateCodec(bytes);
        if (codec is null) return NotAnImage();

        // Площадь — по заголовку этого же кодека: SKCodec пиксели не декодирует. Текст тот же, что у
        // вложений ядра.
        if (ImageDownscaler.PixelCountExceeded(codec.Info, limits.MaxPixels) is { } tooMany)
            return new PhotoRefused(PhotoRefusalReason.TooManyPixels, tooMany);

        using var decoded = Decode(codec, limits.MaxSide);
        if (decoded is null) return NotAnImage();

        // Уменьшение — до поворота: длинная сторона от поворота не меняется, а вертеть меньшую картинку
        // дешевле. SkiaSharp, не сумевший уменьшить, — та же картинка, которую не прочли: отказ значением,
        // а не «внутренняя ошибка» на загрузке.
        if (!TryShrink(decoded, limits.MaxSide, out var shrunk)) return NotAnImage();
        using (shrunk)
        {
            using var image = Orient(shrunk ?? decoded, codec.EncodedOrigin, out var width, out var height);
            if (!TryShrink(image, limits.PreviewSide, out var preview)) return NotAnImage();
            using (preview)
            {
                var facts = ExifReader.Read(bytes);
                return new PhotoPrepared(Jpeg(image), Jpeg(preview ?? image), width, height,
                    facts.Geo, facts.TakenAt, facts.Offset);
            }
        }
    }

    /// <summary>
    /// Декодирование сразу в уменьшенном виде, где формат это умеет: JPEG — масштабом DCT n/8, WebP — любым.
    /// Берётся наименьший масштаб, при котором длинная сторона ещё не меньше <paramref name="maxSide" />:
    /// дальше доуменьшает Mitchell, и качество то же. Снимок с телефона на 48 Мп так занимает в памяти
    /// десятки мегабайт, а не почти двести. PNG масштаба не знает — декодируется целиком.
    /// </summary>
    private static SKBitmap? Decode(SKCodec codec, int maxSide)
    {
        var full = codec.Info;
        var size = new SKSizeI(full.Width, full.Height);
        for (var num = 1; num < 8; num++)
        {
            var scaled = codec.GetScaledDimensions(num / 8f);
            if (Math.Max(scaled.Width, scaled.Height) < maxSide) continue;
            if ((long)scaled.Width * scaled.Height < (long)size.Width * size.Height) size = scaled;
            break;
        }

        try { return SKBitmap.Decode(codec, full.WithSize(size.Width, size.Height)); }
        catch (Exception) { return null; }   // повреждённый файл бросает, а не отвечает null
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

    /// <summary>
    /// Уменьшенная копия в <paramref name="shrunk" />; уже в пределах — там <c>null</c>, и копии нет.
    /// <c>false</c> — уменьшить было нужно, а SkiaSharp не смог.
    /// </summary>
    private static bool TryShrink(SKBitmap source, int maxSide, out SKBitmap? shrunk)
    {
        shrunk = null;
        var (width, height) = ImageDownscaler.Fit(source.Width, source.Height, maxSide);
        if (width == source.Width && height == source.Height) return true;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, source.ColorSpace);
        shrunk = source.Resize(info, ImageDownscaler.Sampling);
        return shrunk is not null;
    }

    /// <summary>
    /// Картинка, повёрнутая по отметке EXIF, — пиксель в пиксель: каждая ориентация — перестановка
    /// осей, без интерполяции. Заодно непрозрачная: PNG с прозрачностью ложится на белое, а не на
    /// чёрное, которым JPEG заменил бы прозрачные пиксели. Исходную не освобождает.
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

        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque, source.ColorSpace));
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.White);
        canvas.SetMatrix(matrix);
        canvas.DrawBitmap(source, 0, 0);
        return result;
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
}
