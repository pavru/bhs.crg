using SkiaSharp;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>Результат уменьшения: байты, тип и итоговые размеры. Null-байты — уменьшать не понадобилось.</summary>
public record DownscaleResult(byte[]? Bytes, string MimeType, int Width, int Height, int SourceWidth, int SourceHeight);

/// <summary>
/// Уменьшение картинки до разумного размера (issue #523).
///
/// Уменьшение — ПРОИЗВОДНАЯ: оригинал остаётся в хранилище, здесь рождается копия. Пока оригинал
/// лежал data-URI в JSONB, положить его было некуда, и уменьшать было нельзя — документы
/// исполнительные, печать и подпись попадают в подписываемый PDF. После переезда в блобы (#522)
/// оригинал и копия сосуществуют дёшево, и вопрос «менять ли данные без спроса» снимается сам.
///
/// Порог 2400 px по длинной стороне — по худшему реалистичному случаю, а не по печати:
/// <list type="bullet">
/// <item>печать ⌀45 мм — ~1350 dpi;</item>
/// <item>факсимиле 60 мм — ~1000 dpi;</item>
/// <item>скан на ширину A4 — ~290 dpi (при 1600 px было бы ~190, и тонкие линии плывут).</item>
/// </list>
///
/// Что делаем осознанно НЕ так, как аватар (<c>downscaleToDataUri</c>): не обрезаем по центру и не
/// переводим всё в JPEG. Круглой печати обрезка срезала бы края, а JPEG убил бы прозрачность —
/// печать ложится ПОВЕРХ текста. Картинка с альфой остаётся PNG.
///
/// Файл, который уже укладывается в порог, не трогаем вовсе: ни уменьшения, ни перекодирования —
/// перекодирование без нужды это потеря качества на ровном месте.
/// </summary>
public static class ImageDownscaler
{
    public const int MaxSide = 2400;

    /// <summary>Качество JPEG для копий без прозрачности. 90 — предел, за которым размер растёт быстрее пользы.</summary>
    private const int JpegQuality = 90;

    /// <summary>Потолок площади: 8000×8000 с запасом покрывает скан A0 при 600 dpi.</summary>
    public const long MaxPixels = 64_000_000;

    /// <summary>
    /// Причина отказа, если картинка слишком велика В ПИКСЕЛЯХ, иначе null (issue #534).
    ///
    /// Предел в байтах её не ограничивает: PNG на 10 МБ разворачивается в памяти в гигабайты
    /// (20000×20000 ≈ 1,6 ГБ), и любой вошедший пользователь мог бы уронить процесс. Размеры читаем
    /// из заголовка — <c>SKCodec</c> не декодирует пиксели.
    /// </summary>
    public static string? PixelCountExceeded(byte[] source, long maxPixels = MaxPixels)
    {
        try
        {
            using var codec = SKCodec.Create(new MemoryStream(source));
            if (codec is null) return null;   // не распознали — уменьшать всё равно не будем
            var pixels = (long)codec.Info.Width * codec.Info.Height;
            return pixels > maxPixels
                ? $"Изображение {codec.Info.Width}×{codec.Info.Height} слишком велико для обработки."
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Уменьшенная копия или «не понадобилось». Нераспознанный формат (в том числе SVG — он
    /// векторный и легковесен) возвращается как «не понадобилось»: портить то, чего не поняли, хуже,
    /// чем оставить как есть.
    /// </summary>
    public static DownscaleResult Downscale(byte[] source, string mimeType, int maxSide = MaxSide)
    {
        // Decode на нераспознанных байтах не возвращает null, а БРОСАЕТ ArgumentNullException
        // («Parameter 'codec'») — поймано тестом на SVG. Ловим: портить то, чего не поняли, хуже,
        // чем оставить как есть.
        SKBitmap? bitmap;
        try { bitmap = SKBitmap.Decode(source); }
        catch (Exception) { bitmap = null; }
        if (bitmap is null) return new DownscaleResult(null, mimeType, 0, 0, 0, 0);
        using var _ = bitmap;

        var longest = Math.Max(bitmap.Width, bitmap.Height);
        if (longest <= maxSide)
            return new DownscaleResult(null, mimeType, bitmap.Width, bitmap.Height, bitmap.Width, bitmap.Height);

        var scale = (double)maxSide / longest;
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));

        // Mitchell, а НЕ SKSamplingOptions.Default (issue #534): Default — это Nearest, то есть
        // выбрасывание строк и столбцов. На чертеже с линиями в 1 пиксель это уничтожает ровно то,
        // ради чего мы вообще подбирали порог 2400 px: линия, попавшая на выброшенную строку,
        // исчезает целиком. Побочно nearest даёт и БОЛЬШИЙ файл — резкие края хуже сжимаются.
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        using var resized = bitmap.Resize(new SKImageInfo(width, height), sampling);
        if (resized is null) return new DownscaleResult(null, mimeType, bitmap.Width, bitmap.Height, bitmap.Width, bitmap.Height);

        // Прозрачность решает формат, а не исходный тип: PNG без альфы незачем держать PNG'ом, а
        // картинку С альфой нельзя отдавать в JPEG — печать ляжет на белом прямоугольнике.
        // Судим по ИСХОДНОЙ картинке: после Resize AlphaType всегда Premul, и быстрый выход по
        // «непрозрачно» не срабатывал бы никогда — попиксельный обход шёл бы на каждую загрузку.
        var hasAlpha = !bitmap.Info.IsOpaque && HasTransparency(resized);
        using var image = SKImage.FromBitmap(resized);
        using var data = hasAlpha
            ? image.Encode(SKEncodedImageFormat.Png, 100)
            : image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);

        return new DownscaleResult(
            data.ToArray(),
            hasAlpha ? "image/png" : "image/jpeg",
            width, height, bitmap.Width, bitmap.Height);
    }

    /// <summary>
    /// Есть ли хоть один непрозрачный пиксель. Читаем буфер целиком, а не через GetPixel: тот на
    /// каждый пиксель уходит в нативный вызов, и на 2400×1800 обход занимал десятки миллисекунд.
    /// </summary>
    private static bool HasTransparency(SKBitmap bitmap)
    {
        if (bitmap.AlphaType == SKAlphaType.Opaque) return false;
        var pixels = bitmap.Pixels;
        foreach (var p in pixels)
            if (p.Alpha < 255) return true;
        return false;
    }
}
