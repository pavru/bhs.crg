using BHS.CRG.Infrastructure.Generation;
using SkiaSharp;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Уменьшение картинки (issue #523). Проверяем ровно то, ради чего оно писалось иначе, чем аватарное:
/// без обрезки, с сохранением прозрачности и без перекодирования того, что уже укладывается.
/// </summary>
public class ImageDownscalerTests
{
    private static byte[] Png(int w, int h, bool transparent = false)
    {
        using var bitmap = new SKBitmap(w, h, transparent ? SKColorType.Rgba8888 : SKColorType.Rgb888x,
            transparent ? SKAlphaType.Unpremul : SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(transparent ? SKColors.Transparent : SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(0, 0, w / 2f, h / 2f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void SmallImage_IsNotTouchedAtAll()
    {
        var result = ImageDownscaler.Downscale(Png(800, 600), "image/png");
        Assert.Null(result.Bytes);   // ни уменьшения, ни перекодирования
        Assert.Equal(800, result.SourceWidth);
    }

    /// <summary>Пропорции сохраняются: обрезка круглой печати срезала бы края.</summary>
    [Fact]
    public void LargeImage_IsScaled_KeepingAspectRatio()
    {
        var result = ImageDownscaler.Downscale(Png(4800, 2400), "image/png");

        Assert.NotNull(result.Bytes);
        Assert.Equal(ImageDownscaler.MaxSide, result.Width);
        Assert.Equal(ImageDownscaler.MaxSide / 2, result.Height);   // 2:1 как было
        Assert.Equal(4800, result.SourceWidth);
    }

    /// <summary>Прозрачность обязана выжить: печать ложится ПОВЕРХ текста, JPEG дал бы белый прямоугольник.</summary>
    [Fact]
    public void TransparentImage_StaysPng()
    {
        var result = ImageDownscaler.Downscale(Png(3000, 3000, transparent: true), "image/png");

        Assert.NotNull(result.Bytes);
        Assert.Equal("image/png", result.MimeType);
        using var decoded = SKBitmap.Decode(result.Bytes);
        Assert.NotEqual(SKAlphaType.Opaque, decoded.AlphaType);
    }

    /// <summary>Без альфы держать PNG незачем — копия заметно легче в JPEG.</summary>
    [Fact]
    public void OpaqueImage_BecomesJpeg_AndGetsSmaller()
    {
        var source = Png(4000, 3000);
        var result = ImageDownscaler.Downscale(source, "image/png");

        Assert.NotNull(result.Bytes);
        Assert.Equal("image/jpeg", result.MimeType);
        Assert.True(result.Bytes!.Length < source.Length,
            $"копия {result.Bytes.Length} Б не легче оригинала {source.Length} Б");
    }

    /// <summary>Нераспознанное (SVG, битые байты) не трогаем: портить непонятое хуже, чем оставить.</summary>
    [Fact]
    public void UndecodableInput_IsLeftAlone()
    {
        var result = ImageDownscaler.Downscale("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(), "image/svg+xml");
        Assert.Null(result.Bytes);
    }

    /// <summary>
    /// Уменьшение пикселей НЕ гарантирует уменьшения байтов: хорошо сжимаемая картинка после
    /// пересжатия в JPEG бывает в разы тяжелее. Сам уменьшатель об этом не судит — решение «брать ли
    /// копию» принимает вызывающий, и здесь мы фиксируем, что данные для такого решения он получает.
    /// </summary>
    [Fact]
    public void ResultCarriesSizesForTheCallerToDecide()
    {
        var source = Png(4000, 3000);
        var result = ImageDownscaler.Downscale(source, "image/png");

        Assert.NotNull(result.Bytes);
        Assert.Equal(4000, result.SourceWidth);
        Assert.Equal(3000, result.SourceHeight);
        Assert.True(result.Width <= ImageDownscaler.MaxSide);
    }
}
