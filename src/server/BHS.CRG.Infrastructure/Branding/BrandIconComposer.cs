using BHS.CRG.Application.Branding;
using SkiaSharp;

namespace BHS.CRG.Infrastructure.Branding;

/// <param name="Icons">Готовые PNG — по одному на каждый <see cref="BrandIconKind" />.</param>
/// <param name="Aspect">Ширина к высоте видимой части логотипа — того, что вписано в значок.</param>
/// <param name="Tile">Цвет плитки, на которой стоит логотип.</param>
public sealed record BrandIconSet(
    IReadOnlyDictionary<BrandIconKind, byte[]> Icons, double Aspect, SKColor Tile);

/// <summary>
/// Значки из логотипа (issue #1315): логотип целиком, без обрезки, по центру плитки.
///
/// <para>На входе — логотип, уже растрированный в точки (это делает <see cref="ILogoRasterizer" />:
/// четыре формата, из них один векторный). Здесь только геометрия и цвет — чистая функция от
/// картинки, поэтому правила ниже проверяются тестами без Typst и без базы.</para>
///
/// <para><b>Значок — всегда логотип на плитке</b> (решения Дизайнера от 13.10.2026). Прозрачный
/// значок проваливается целиком в двух случаях — тёмный логотип на тёмной вкладке и светлый на
/// светлой, — а построенная картинка, в отличие от стандартного SVG, перекраситься под тему не
/// может. Цвет плитки берётся из самого логотипа, тремя шагами — см. <see cref="TileFor" />.</para>
/// </summary>
public static class BrandIconComposer
{
    /// <summary>Белая плитка — как у стандартных значков: смена логотипа не меняет тон плитки.</summary>
    public static readonly SKColor LightTile = new(0xff, 0xff, 0xff);

    /// <summary>
    /// Тёмная плитка под светлый логотип. Это поверхность тёмной темы приложения (<c>--f-bg1</c>,
    /// <c>#111318</c>) — то, на чём светлый логотип уже стоит в шапке.
    /// </summary>
    public static readonly SKColor DarkTile = new(0x11, 0x13, 0x18);

    /// <summary>
    /// Что нарисовать. <paramref name="Share" /> — доля стороны под логотип: сторона квадрата, в
    /// который он вписан, либо диаметр круга при <paramref name="Circle" />.
    /// </summary>
    public sealed record Spec(BrandIconKind Kind, int Side, double Share, bool Rounded, bool Circle = false);

    /// <summary>
    /// Размеры и поля. Числа — Дизайнера, каждое со своей причиной:
    /// <list type="bullet">
    /// <item>вкладка — 28 из 32: на экране это 14 из 16, поле ровно в одну точку. Без поля логотип
    /// упирается в край плитки, второе съело бы четверть площади;</item>
    /// <item>значки приложения — 76 %: логотип заказчика чаще широкий, а показывают их мелко;</item>
    /// <item>iPhone — 72 %, без скругления: скругляет система, второй радиус дал бы обгрызенные углы;</item>
    /// <item>маскируемый — круг 80 %: безопасная зона маски — круг, и квадрат 80 % режется по углам
    /// (его диагональ — 113 %). Вписывание по диагонали даёт квадратному логотипу те же 56,6 %, что
    /// у стандартного значка, а широкому — на треть больше.</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyList<Spec> Specs =
    [
        new(BrandIconKind.Tab, 32, 0.875, Rounded: true),
        new(BrandIconKind.App192, 192, 0.76, Rounded: true),
        new(BrandIconKind.App512, 512, 0.76, Rounded: true),
        new(BrandIconKind.Apple, 180, 0.72, Rounded: false),
        new(BrandIconKind.Maskable512, 512, 0.80, Rounded: false, Circle: true),
    ];

    /// <summary>Скругление плитки в долях стороны — как у стандартных значков.</summary>
    private const float TileRadius = 0.22f;

    /// <summary>
    /// С какой непрозрачности точка считается видимой. Не ноль: шум в одну-две единицы по всему
    /// холсту не давал бы срезать пустые поля, и логотип вставал бы мелко.
    /// </summary>
    private const byte Visible = 8;

    /// <summary>Допуск «одного цвета» на канал — под сжатие JPEG.</summary>
    private const int SameColor = 8;

    /// <summary>
    /// С какой средней яркости логотип считается светлым. Порог сдвинут к белому нарочно: выбор по
    /// наибольшему контрасту увёл бы на тёмную плитку и бирюзу, и оранжевый. При 0,5 туда уходят
    /// белый, светло-серый и жёлтый.
    /// </summary>
    private const double LightFrom = 0.5;

    /// <summary>
    /// Значки из растрированного логотипа; <c>null</c> — в картинке нет ни одной видимой точки
    /// либо она не читается.
    /// </summary>
    public static BrandIconSet? Compose(byte[] raster)
    {
        using var codec = SKCodec.Create(new MemoryStream(raster));
        if (codec is null) return null;

        // Без предумножения: цвет точки нужен как он есть, а не умноженным на прозрачность.
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var plain = SKBitmap.Decode(codec, info);
        if (plain is null) return null;

        var pixels = plain.GetPixelSpan();
        if (VisibleBox(pixels, plain.RowBytes, plain.Width, plain.Height) is not { } box) return null;

        var tile = TileFor(pixels, plain.RowBytes, box);

        // Прозрачные края срезаются до вписывания. Это не обрезка логотипа: файл с пустым холстом
        // вокруг знака иначе дал бы мелкий значок.
        using var trimmed = new SKBitmap();
        if (!plain.ExtractSubset(trimmed, box)) return null;
        using var logo = SKImage.FromBitmap(trimmed);

        var icons = Specs.ToDictionary(spec => spec.Kind, spec => Draw(spec, logo, tile));
        return new BrandIconSet(icons, box.Width / (double)box.Height, tile);
    }

    /// <summary>Прямоугольник видимых точек; <c>null</c> — видимых нет.</summary>
    private static SKRectI? VisibleBox(ReadOnlySpan<byte> pixels, int stride, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            for (var x = 0; x < width; x++)
            {
                if (row[x * 4 + 3] < Visible) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                bottom = y;
            }
        }

        return right < 0 ? null : new SKRectI(left, top, right + 1, bottom + 1);
    }

    /// <summary>
    /// Цвет плитки — из самого логотипа:
    /// <list type="number">
    /// <item>у логотипа свой фон (четыре угла непрозрачны и одного цвета) — плитка его продолжает.
    /// Иначе JPEG с цветной заливкой встал бы на белом квадрате «флагом», а в круглой маске —
    /// полосой через круг. Почти белый фон становится чисто белым;</item>
    /// <item>логотип светлый — плитка тёмная. Белый логотип на белой плитке — пустой квадрат при
    /// «успешном» построении, то есть отказ, переодетый в результат;</item>
    /// <item>иначе — белая.</item>
    /// </list>
    /// </summary>
    private static SKColor TileFor(ReadOnlySpan<byte> pixels, int stride, SKRectI box) =>
        OwnBackground(pixels, stride, box)
        ?? (MeanLuminance(pixels, stride, box) >= LightFrom ? DarkTile : LightTile);

    private static SKColor? OwnBackground(ReadOnlySpan<byte> pixels, int stride, SKRectI box)
    {
        // Углы берём с отступом: крайний ряд при растрировании бывает полупрозрачным.
        var inset = Math.Max(2, Math.Min(box.Width, box.Height) / 100);
        if (box.Width <= inset * 2 || box.Height <= inset * 2) inset = 0;

        Span<int> xs = [box.Left + inset, box.Right - 1 - inset];
        Span<int> ys = [box.Top + inset, box.Bottom - 1 - inset];
        Span<int> sum = [0, 0, 0];
        Span<byte> corners = stackalloc byte[12];
        var n = 0;
        foreach (var y in ys)
        foreach (var x in xs)
        {
            var p = pixels.Slice(y * stride + x * 4, 4);
            if (p[3] < 250) return null;
            for (var c = 0; c < 3; c++) { corners[n * 3 + c] = p[c]; sum[c] += p[c]; }
            n++;
        }

        Span<int> mean = [sum[0] / 4, sum[1] / 4, sum[2] / 4];
        for (var i = 0; i < 4; i++)
        for (var c = 0; c < 3; c++)
            if (Math.Abs(corners[i * 3 + c] - mean[c]) > SameColor) return null;

        return mean[0] >= 255 - SameColor && mean[1] >= 255 - SameColor && mean[2] >= 255 - SameColor
            ? LightTile
            : new SKColor((byte)mean[0], (byte)mean[1], (byte)mean[2]);
    }

    /// <summary>
    /// Средняя относительная яркость видимых точек (по WCAG), взвешенная по непрозрачности:
    /// полупрозрачный край знака весит меньше, чем его тело.
    /// </summary>
    private static double MeanLuminance(ReadOnlySpan<byte> pixels, int stride, SKRectI box)
    {
        double light = 0, weight = 0;
        for (var y = box.Top; y < box.Bottom; y++)
        {
            var row = pixels.Slice(y * stride + box.Left * 4, box.Width * 4);
            for (var x = 0; x < box.Width; x++)
            {
                var a = row[x * 4 + 3];
                if (a < Visible) continue;
                light += a * (0.2126 * Linear[row[x * 4]] + 0.7152 * Linear[row[x * 4 + 1]] + 0.0722 * Linear[row[x * 4 + 2]]);
                weight += a;
            }
        }

        return weight > 0 ? light / weight : 0;
    }

    /// <summary>sRGB → линейная яркость канала, таблицей: считается для миллиона точек.</summary>
    private static readonly double[] Linear = [.. Enumerable.Range(0, 256).Select(v =>
    {
        var c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    })];

    private static byte[] Draw(Spec spec, SKImage logo, SKColor tile)
    {
        var side = spec.Side;
        using var surface = SKSurface.Create(new SKImageInfo(side, side, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using (var paint = new SKPaint { Color = tile, IsAntialias = true })
        {
            if (spec.Rounded) canvas.DrawRoundRect(SKRect.Create(side, side), side * TileRadius, side * TileRadius, paint);
            else canvas.DrawRect(SKRect.Create(side, side), paint);
        }

        // Вписываем целиком: в квадрат — по большей стороне, в круг — по диагонали.
        var scale = spec.Circle
            ? side * spec.Share / Math.Sqrt((double)logo.Width * logo.Width + (double)logo.Height * logo.Height)
            : side * spec.Share / Math.Max(logo.Width, logo.Height);
        // До целых точек: на значке в 32 точки полточки сдвига — это размытая кромка.
        var width = Math.Max(1, (int)Math.Round(logo.Width * scale));
        var height = Math.Max(1, (int)Math.Round(logo.Height * scale));
        var dest = SKRect.Create((side - width) / 2, (side - height) / 2, width, height);

        // Уменьшаем ступенями вдвое: один шаг с 1024 до 28 точек читает по четыре соседа на точку,
        // то есть пропускает почти всю картинку, и тонкие линии логотипа рассыпаются в шум.
        var current = logo;
        while (current.Width >= width * 2 && current.Height >= height * 2)
        {
            var half = Halved(current);
            if (!ReferenceEquals(current, logo)) current.Dispose();
            current = half;
        }

        canvas.DrawImage(current, dest, new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (!ReferenceEquals(current, logo)) current.Dispose();

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static SKImage Halved(SKImage source)
    {
        var width = Math.Max(1, source.Width / 2);
        var height = Math.Max(1, source.Height / 2);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(source, SKRect.Create(width, height), new SKSamplingOptions(SKFilterMode.Linear));
        return surface.Snapshot();
    }
}
