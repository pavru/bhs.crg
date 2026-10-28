using System.Security.Cryptography;
using BHS.CRG.Application.Branding;
using SkiaSharp;

namespace BHS.CRG.Infrastructure.Branding;

/// <param name="Icons">Готовые PNG — по одному на каждый <see cref="BrandIconKind" />.</param>
/// <param name="Aspect">Ширина к высоте видимой части логотипа — того, что вписано в значок.</param>
/// <param name="Tile">Цвет плитки, на которой стоит логотип.</param>
/// <param name="Stamp">
/// Отпечаток набора — по содержимому всех значков. Идёт в метку версии и в тег ответа: значок —
/// функция не только от логотипа, но и от правил ниже и от Typst. Без отпечатка обновление
/// приложения, изменившее поля или правило плитки, давало бы другие картинки под прежней меткой —
/// и браузер, переспросив с прежним тегом, получал бы 304 и держал старый значок бессрочно.
/// </param>
public sealed record BrandIconSet(
    IReadOnlyDictionary<BrandIconKind, byte[]> Icons, double Aspect, SKColor Tile, string Stamp);

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
    /// Тёмная плитка под светлый логотип. Это поверхность тёмной темы приложения (роль MD3
    /// <c>surface</c>, <c>#111318</c>) — то, на чём светлый логотип уже стоит в шапке. Сверяет с
    /// описанием темы клиента <c>md3-theme.test.mjs</c>.
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

    /// <summary>С какой непрозрачности точка считается залитой — частью сплошного фона.</summary>
    private const byte Opaque = 250;

    /// <summary>
    /// С какой доли залитых точек прямоугольник логотипа считается залитым целиком. Не единица —
    /// допуск на случайные незалитые точки, и только на них: скругление углов он не обслуживает
    /// (плашку с заметным радиусом раньше отсекает прозрачный угол, а у прошедшей проверку углов
    /// потеря площади — сотые доли процента). Цена допуска названа прямо: вырез меньше тысячной
    /// площади знаком не считается, и плашка с ним получает плитку своего цвета.
    /// </summary>
    private const double FilledFrom = 0.999;

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
        // Без предумножения: цвет точки нужен как он есть, а не умноженным на прозрачность.
        using var plain = Decoded(raster, SKAlphaType.Unpremul);
        if (plain is null) return null;

        var pixels = plain.GetPixelSpan();
        if (VisibleBox(pixels, plain.RowBytes, plain.Width, plain.Height) is not { } box) return null;

        var tile = TileFor(pixels, plain.RowBytes, box);

        // ⚠️ Рисуется логотип из ДРУГОГО растра — предумноженного. Непредумноженный Skia сглаживает
        // до умножения на прозрачность: край знака смешивается с цветом прозрачных точек (у растра
        // от Typst это чёрный, 0-0-0-0), и вокруг цветного логотипа на белой плитке встаёт серая
        // кайма в две-три точки (ревью PR #1320: у оранжевого круга на краю выходило 215-192-163
        // вместо красного канала 255). Разбор цвета выше остаётся на непредумноженном.
        using var drawn = Decoded(raster, SKAlphaType.Premul);
        if (drawn is null) return null;

        // Прозрачные края срезаются до вписывания. Это не обрезка логотипа: файл с пустым холстом
        // вокруг знака иначе дал бы мелкий значок.
        using var trimmed = new SKBitmap();
        if (!drawn.ExtractSubset(trimmed, box)) return null;
        using var logo = SKImage.FromBitmap(trimmed);

        var icons = Specs.ToDictionary(spec => spec.Kind, spec => Draw(spec, logo, tile));
        return new BrandIconSet(icons, box.Width / (double)box.Height, tile, StampOf(icons));
    }

    private static SKBitmap? Decoded(byte[] raster, SKAlphaType alpha)
    {
        using var codec = SKCodec.Create(new MemoryStream(raster));
        return codec is null
            ? null
            : SKBitmap.Decode(codec, new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, alpha));
    }

    /// <summary>Меньше этого по большей стороне видимой части — значку не хватает точек.</summary>
    private const int EnoughSide = 400;

    /// <summary>До какой большей стороны доводит видимую часть второй проход: самый крупный значок.</summary>
    private const int WantedSide = 512;

    /// <summary>
    /// Предел увеличения — осторожность, а не замер: рисует ли Typst только попавшее на страницу
    /// или весь SVG в размере показа, не проверялось, а во втором случае вчетверо — это уже холст
    /// 4096 точек внутри его процесса, и дальше расход памяти растёт квадратом. Знак мельче восьмой
    /// части холста остаётся растянутым — но уже не втрое.
    /// </summary>
    private const double LargestScale = 4;

    /// <summary>
    /// Какую часть холста стоит растрировать заново, крупнее; <c>null</c> — точек хватает (или
    /// картинка не читается: тогда отказ назовёт <see cref="Compose" />).
    ///
    /// <para>Самому крупному значку нужно 389 точек по большей стороне логотипа (76 % от 512). У
    /// логотипа с пустыми полями видимая часть первого растра меньше — см. <see cref="LogoView" />.
    /// Спрашивать это имеет смысл только у векторного логотипа: растровому второй проход точек не
    /// прибавит.</para>
    /// </summary>
    public static LogoView? CloserView(byte[] raster)
    {
        using var plain = Decoded(raster, SKAlphaType.Unpremul);
        if (plain is null) return null;
        if (VisibleBox(plain.GetPixelSpan(), plain.RowBytes, plain.Width, plain.Height) is not { } box) return null;

        var larger = Math.Max(box.Width, box.Height);
        if (larger >= EnoughSide) return null;

        // С запасом в две точки: крайний ряд знака при растрировании полупрозрачен, и ровно по
        // границе страницы он срезался бы пополам.
        const int pad = 2;
        int left = Math.Max(0, box.Left - pad), top = Math.Max(0, box.Top - pad);
        int right = Math.Min(plain.Width, box.Right + pad), bottom = Math.Min(plain.Height, box.Bottom + pad);
        return new LogoView(left, top, right - left, bottom - top, Math.Min(LargestScale, WantedSide / (double)larger));
    }

    private static string StampOf(IReadOnlyDictionary<BrandIconKind, byte[]> icons)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var kind in Enum.GetValues<BrandIconKind>()) hash.AppendData(icons[kind]);
        return Convert.ToHexStringLower(hash.GetHashAndReset())[..8];
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
    /// <item>у логотипа свой фон (прямоугольник залит целиком, а четыре его угла одного цвета) —
    /// плитка его продолжает. Иначе JPEG с цветной заливкой встал бы на белом квадрате «флагом», а
    /// в круглой маске — полосой через круг. Почти белый фон становится чисто белым. Залит, а углы
    /// разные (скан, полоса вдоль края, градиент) — плитка белая: продолжать нечего;</item>
    /// <item>логотип светлый — плитка тёмная. Белый логотип на белой плитке — пустой квадрат при
    /// «успешном» построении, то есть отказ, переодетый в результат;</item>
    /// <item>иначе — белая.</item>
    /// </list>
    /// </summary>
    private static SKColor TileFor(ReadOnlySpan<byte> pixels, int stride, SKRectI box) =>
        OwnBackground(pixels, stride, box)
        ?? (MeanLuminance(pixels, stride, box) >= LightFrom ? DarkTile : LightTile);

    /// <summary>
    /// Цвет собственного фона логотипа; <c>null</c> — фона у него нет.
    ///
    /// <para>⚠️ Признак фона — ЗАЛИТЫЙ прямоугольник, а не четыре занятых угла; углы дают только
    /// цвет. У надписи с прямоугольными крайними буквами («НИИ», «HILTI»), у рамки и у плашки с
    /// буквами-вырезами углы тоже непрозрачны и одного цвета, а фона нет: плитка взяла бы цвет
    /// самого знака, и значок вышел бы сплошным одноцветным квадратом при «успешном» построении
    /// (проверка перед PR). Такие логотипы уходят в правила яркости — тёмный на белую плитку,
    /// светлый на тёмную.</para>
    ///
    /// <para>⚠️ Залитый прямоугольник в правила яркости НЕ уходит никогда, даже без единого фона.
    /// Яркость там меряла бы фон, а не знак: тёмная надпись на своём белом фоне с цветной полосой
    /// вдоль края (углы разные) получала бы ТЁМНУЮ плитку — белый прямоугольник на почти чёрном,
    /// «флаг» с наибольшим контрастом. Правило яркости — для знака на прозрачном холсте; залитому
    /// логотипу без единого фона — белая плитка: пустым квадратом он не станет, контраст у него
    /// внутри.</para>
    /// </summary>
    private static SKColor? OwnBackground(ReadOnlySpan<byte> pixels, int stride, SKRectI box)
    {
        // С отступом от края: крайний ряд при растрировании бывает полупрозрачным — он съел бы
        // порог залитости у узкой плашки, а угол в нём читался бы не тем цветом.
        var inset = Math.Max(2, Math.Min(box.Width, box.Height) / 100);
        if (box.Width <= inset * 2 || box.Height <= inset * 2) inset = 0;

        int width = box.Width - inset * 2, height = box.Height - inset * 2;
        long filled = 0;
        for (var y = box.Top + inset; y < box.Bottom - inset; y++)
        {
            var row = pixels.Slice(y * stride + (box.Left + inset) * 4, width * 4);
            for (var x = 0; x < width; x++)
                if (row[x * 4 + 3] >= Opaque) filled++;
        }

        if (filled < (long)width * height * FilledFrom) return null;

        Span<int> xs = [box.Left + inset, box.Right - 1 - inset];
        Span<int> ys = [box.Top + inset, box.Bottom - 1 - inset];
        Span<int> sum = [0, 0, 0];
        Span<byte> corners = stackalloc byte[12];
        var n = 0;
        foreach (var y in ys)
        foreach (var x in xs)
        {
            var p = pixels.Slice(y * stride + x * 4, 4);
            if (p[3] < Opaque) return null;
            for (var c = 0; c < 3; c++) { corners[n * 3 + c] = p[c]; sum[c] += p[c]; }
            n++;
        }

        Span<int> mean = [sum[0] / 4, sum[1] / 4, sum[2] / 4];
        for (var i = 0; i < 4; i++)
        for (var c = 0; c < 3; c++)
            if (Math.Abs(corners[i * 3 + c] - mean[c]) > SameColor) return LightTile;

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
