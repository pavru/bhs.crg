using System.Text;
using SkiaSharp;

namespace BHS.CRG.Tests.Branding;

/// <summary>
/// Образцы логотипов и замеры значков для тестов #1315.
///
/// <para>Образец — тело знака и четыре красные метки, по одной у середины каждой стороны, вплотную
/// к краю. По меткам и видно, обрезан ли логотип: их общий прямоугольник в значке обязан совпасть с
/// местом, куда логотип вписан. Сплошной рамкой метки не сделаны нарочно: рамка заняла бы углы, а
/// одноцветные непрозрачные углы — это «у логотипа свой фон», другое правило плитки.</para>
/// </summary>
public static class BrandIconSamples
{
    public static readonly SKColor Body = new(20, 60, 140);
    public static readonly SKColor Mark = new(220, 0, 0);

    /// <param name="background">Заливка холста; <c>null</c> — прозрачный.</param>
    /// <param name="margin">Пустые поля вокруг знака, в точках: холст больше знака на них.</param>
    public static byte[] Logo(
        int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png,
        SKColor? background = null, SKColor? body = null, int margin = 0)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            width + margin * 2, height + margin * 2, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background ?? SKColors.Transparent);
        canvas.Translate(margin, margin);

        var m = Math.Max(4, Math.Min(width, height) / 10);
        using var paint = new SKPaint { Color = body ?? Body, IsAntialias = false };
        canvas.DrawRect(SKRect.Create(m * 2, m * 2, width - m * 4, height - m * 4), paint);

        paint.Color = Mark;
        canvas.DrawRect(SKRect.Create(0, (height - m) / 2f, m, m), paint);
        canvas.DrawRect(SKRect.Create(width - m, (height - m) / 2f, m, m), paint);
        canvas.DrawRect(SKRect.Create((width - m) / 2f, 0, m, m), paint);
        canvas.DrawRect(SKRect.Create((width - m) / 2f, height - m, m, m), paint);
        canvas.Flush();

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    /// <summary>Тот же образец в SVG: только <c>viewBox</c>, без размеров — как отдают редакторы.</summary>
    public static byte[] Svg(int width, int height)
    {
        var m = Math.Max(4, Math.Min(width, height) / 10);
        string Rect(double x, double y, double w, double h, string fill) =>
            FormattableString.Invariant($"<rect x=\"{x}\" y=\"{y}\" width=\"{w}\" height=\"{h}\" fill=\"{fill}\"/>");

        return Encoding.UTF8.GetBytes(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\">"
            + Rect(m * 2, m * 2, width - m * 4, height - m * 4, "#143c8c")
            + Rect(0, (height - m) / 2.0, m, m, "#dc0000")
            + Rect(width - m, (height - m) / 2.0, m, m, "#dc0000")
            + Rect((width - m) / 2.0, 0, m, m, "#dc0000")
            + Rect((width - m) / 2.0, height - m, m, m, "#dc0000")
            + "</svg>");
    }

    public static SKBitmap Decode(byte[] png)
    {
        using var codec = SKCodec.Create(new MemoryStream(png))
            ?? throw new Xunit.Sdk.XunitException("картинка не читается");
        return SKBitmap.Decode(codec, new SKImageInfo(
            codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    }

    public static (int Width, int Height) Size(byte[] png)
    {
        using var bitmap = Decode(png);
        return (bitmap.Width, bitmap.Height);
    }

    public static SKColor Pixel(byte[] png, int x, int y)
    {
        using var bitmap = Decode(png);
        return bitmap.GetPixel(x, y);
    }

    /// <summary>
    /// Прямоугольник красных меток в значке — то место, куда вписан логотип. Порог широкий: JPEG
    /// метки размывает, а уменьшение — смешивает с соседями.
    /// </summary>
    public static SKRectI Marks(byte[] png)
    {
        using var bitmap = Decode(png);
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var p = bitmap.GetPixel(x, y);
            if (p.Alpha < 200 || p.Red < 150 || p.Green > 90 || p.Blue > 90) continue;
            if (x < left) left = x;
            if (x > right) right = x;
            if (y < top) top = y;
            if (y > bottom) bottom = y;
        }

        return right < 0 ? SKRectI.Empty : new SKRectI(left, top, right + 1, bottom + 1);
    }
}
