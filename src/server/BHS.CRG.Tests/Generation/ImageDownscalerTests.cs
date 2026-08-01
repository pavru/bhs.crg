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

    /// <summary>
    /// Тонкие линии обязаны пережить уменьшение (issue #534). С sampling по умолчанию (Nearest)
    /// картинка с линиями в 1 пиксель выходила ПУСТОЙ: строки и столбцы просто выбрасывались. Это
    /// ровно тот вред, ради предотвращения которого подбирался порог 2400 px, — и его не ловил ни
    /// один тест, потому что размеры и вес при этом выглядели правильно.
    /// </summary>
    [Fact]
    public void ThinLines_SurviveDownscale()
    {
        const int w = 4800, h = 3400;
        byte[] source;
        using (var bitmap = new SKBitmap(w, h, SKColorType.Rgb888x, SKAlphaType.Opaque))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.White);
                using var paint = new SKPaint { Color = SKColors.Black };
                for (var x = 1; x < w; x += 2) canvas.DrawRect(x, 0, 1, h, paint);   // линии в 1 px
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            source = data.ToArray();
        }

        var result = ImageDownscaler.Downscale(source, "image/png");
        Assert.NotNull(result.Bytes);

        using var small = SKBitmap.Decode(result.Bytes);
        var dark = 0;
        for (var x = 0; x < small.Width; x++)
            if (small.GetPixel(x, small.Height / 2).Red < 200) dark++;
        Assert.True(dark > small.Width / 4,
            $"после уменьшения осталось {dark} тёмных пикселей из {small.Width} — линии потеряны");
    }

    /// <summary>
    /// Предел в байтах не ограничивает пиксели: PNG на единицы мегабайт разворачивается в памяти в
    /// гигабайты, и любой вошедший пользователь мог бы уронить процесс. Размеры читаются из
    /// заголовка, без декодирования (issue #534).
    /// </summary>
    [Fact]
    public void HugeDimensions_AreRefusedBeforeDecoding()
    {
        // 12000×12000 = 144 млн пикселей при пределе 64 млн; сами байты крошечные — картинка пустая.
        var huge = Png(12000, 12000);
        Assert.NotNull(ImageDownscaler.PixelCountExceeded(huge));
        Assert.Null(ImageDownscaler.PixelCountExceeded(Png(2000, 2000)));
    }
}
