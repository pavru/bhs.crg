using BHS.CRG.Application.Branding;
using BHS.CRG.Infrastructure.Branding;
using SkiaSharp;
using static BHS.CRG.Tests.Branding.BrandIconSamples;

namespace BHS.CRG.Tests.Branding;

/// <summary>
/// Геометрия и цвет значков из логотипа (issue #1315) — чистая функция от картинки, без Typst и без
/// базы. Здесь держится обещание задачи: логотип вписан ЦЕЛИКОМ, без обрезки, по центру.
/// </summary>
public class BrandIconComposerTests
{
    private static BrandIconSet Compose(byte[] logo) =>
        BrandIconComposer.Compose(logo) ?? throw new Xunit.Sdk.XunitException("значки не построены");

    [Fact]
    public void Набор_полон_и_каждый_значок_своего_размера()
    {
        var set = Compose(Logo(800, 200));

        Assert.Equal(Enum.GetValues<BrandIconKind>().Order(), set.Icons.Keys.Order());
        foreach (var spec in BrandIconComposer.Specs)
            Assert.Equal((spec.Side, spec.Side), Size(set.Icons[spec.Kind]));
    }

    /// <summary>
    /// Сторож задачи: широкая надпись вписана целиком. Метки стоят у самых краёв логотипа — их общий
    /// прямоугольник в значке обязан быть ровно тем местом, куда логотип вписан: 76 % стороны по
    /// ширине, вчетверо меньше по высоте, по центру.
    /// </summary>
    [Fact]
    public void Широкий_логотип_вписан_целиком_и_по_центру()
    {
        var marks = Marks(Compose(Logo(800, 200)).Icons[BrandIconKind.App512]);

        Assert.InRange(marks.Width, 387, 391);    // 512 × 0,76 = 389
        Assert.InRange(marks.Height, 95, 99);     // 389 / 4
        Assert.InRange(marks.Left + marks.Right, 510, 514);
        Assert.InRange(marks.Top + marks.Bottom, 510, 514);
    }

    [Fact]
    public void Высокий_логотип_вписан_по_высоте()
    {
        var marks = Marks(Compose(Logo(200, 600)).Icons[BrandIconKind.App512]);

        Assert.InRange(marks.Height, 387, 391);
        Assert.InRange(marks.Width, 128, 132);    // 389 / 3
    }

    /// <summary>
    /// Пустой холст вокруг знака срезается до вписывания — иначе файл с полями дал бы мелкий значок.
    /// Это не обрезка логотипа: метки по-прежнему целы и занимают то же место.
    /// </summary>
    [Fact]
    public void Прозрачные_поля_файла_не_уменьшают_логотип()
    {
        var bare = Compose(Logo(800, 200));
        var padded = Compose(Logo(800, 200, margin: 300));

        Assert.Equal(Marks(bare.Icons[BrandIconKind.App512]), Marks(padded.Icons[BrandIconKind.App512]));
        Assert.Equal(4.0, padded.Aspect, 2);
    }

    /// <summary>
    /// Маска гарантирует только центральный круг в 80 % стороны, поэтому логотип вписан в КРУГ:
    /// квадрат 80 % резался бы по углам. Все четыре угла логотипа — внутри круга.
    /// </summary>
    [Theory]
    [InlineData(800, 200)]
    [InlineData(400, 400)]
    [InlineData(200, 600)]
    public void Маскируемый_значок_держит_логотип_в_безопасном_круге(int width, int height)
    {
        var marks = Marks(Compose(Logo(width, height)).Icons[BrandIconKind.Maskable512]);

        var diagonal = Math.Sqrt(marks.Width * (double)marks.Width + marks.Height * (double)marks.Height);
        Assert.InRange(diagonal, 512 * 0.8 - 6, 512 * 0.8 + 2);
        Assert.InRange(marks.Left + marks.Right, 510, 514);
        Assert.InRange(marks.Top + marks.Bottom, 510, 514);
    }

    /// <summary>Вкладка: 28 из 32 точек — на экране это поле ровно в одну точку.</summary>
    [Fact]
    public void Значок_вкладки_оставляет_поле_в_одну_экранную_точку()
    {
        var marks = Marks(Compose(Logo(400, 400)).Icons[BrandIconKind.Tab]);

        Assert.Equal(new SKRectI(2, 2, 30, 30), marks);
    }

    [Fact]
    public void Тёмный_логотип_встаёт_на_белую_плитку()
    {
        var set = Compose(Logo(800, 200));

        Assert.Equal(BrandIconComposer.LightTile, set.Tile);
        Assert.Equal(BrandIconComposer.LightTile, Pixel(set.Icons[BrandIconKind.Apple], 90, 10));
    }

    /// <summary>
    /// Белый логотип на белой плитке — пустой квадрат при «успешном» построении: отказ, переодетый в
    /// результат. Светлому логотипу — тёмная плитка.
    /// </summary>
    [Fact]
    public void Светлый_логотип_встаёт_на_тёмную_плитку()
    {
        var white = Logo(800, 200, body: SKColors.White);

        var set = Compose(white);

        Assert.Equal(BrandIconComposer.DarkTile, set.Tile);
        Assert.Equal(BrandIconComposer.DarkTile, Pixel(set.Icons[BrandIconKind.Apple], 90, 10));
    }

    /// <summary>Порог сдвинут к белому: цветной логотип средней яркости остаётся на белой плитке.</summary>
    [Fact]
    public void Цветной_логотип_средней_яркости_остаётся_на_белой_плитке()
    {
        var teal = Logo(800, 200, body: new SKColor(0x10, 0x9e, 0x92));

        Assert.Equal(BrandIconComposer.LightTile, Compose(teal).Tile);
    }

    /// <summary>
    /// У логотипа свой фон — плитка его продолжает: иначе цветная заливка встала бы на белом
    /// квадрате «флагом», а в круглой маске — полосой через круг.
    /// </summary>
    [Fact]
    public void Логотип_со_своим_фоном_продолжается_плиткой()
    {
        var fill = new SKColor(0x10, 0x9e, 0x92);
        var set = Compose(Logo(800, 200, background: fill, body: SKColors.White));

        Assert.Equal(fill, set.Tile);
        // Плитка и логотип слились: над логотипом и в самом углу значка — один цвет.
        Assert.Equal(fill, Pixel(set.Icons[BrandIconKind.Maskable512], 256, 20));
        Assert.Equal(fill, Pixel(set.Icons[BrandIconKind.Maskable512], 3, 3));
    }

    /// <summary>
    /// Порог светлого — с обеих сторон и вплотную: серый чуть темнее порога остаётся на белой
    /// плитке, чуть светлее — уходит на тёмную. Далёкие образцы (белый, бирюза) сдвига порога не
    /// заметили бы, а сдвиг — это светло-серый логотип на белом, пустой квадрат.
    /// </summary>
    [Theory]
    [InlineData(179, false)]   // яркость ≈ 0,45
    [InlineData(196, true)]    // яркость ≈ 0,55
    public void Порог_светлого_логотипа_стоит_на_половине_яркости(byte gray, bool dark)
    {
        var set = Compose(Logo(800, 200, body: new SKColor(gray, gray, gray)));

        Assert.Equal(dark ? BrandIconComposer.DarkTile : BrandIconComposer.LightTile, set.Tile);
    }

    /// <summary>
    /// Занятые углы — ещё не фон. У буквы «Н» все четыре угла непрозрачны и одного цвета, а середина
    /// пуста: плитка цвета самого знака дала бы сплошной одноцветный квадрат при «успешном»
    /// построении — отказ, переодетый в результат. Так выходило у одноцветной надписи с
    /// прямоугольными крайними буквами («НИИ», «HILTI»). Такой знак идёт в правила яркости.
    /// </summary>
    [Fact]
    public void Знак_с_занятыми_углами_не_принимается_за_свой_фон()
    {
        var dark = Compose(LetterH(Body));

        Assert.Equal(BrandIconComposer.LightTile, dark.Tile);
        // И знак на плитке ВИДЕН. Он вписан в 389 точек по центру значка 512 (от 61 до 451): в
        // стойке буквы — её цвет, в просвете над перекладиной — плитка.
        Assert.Equal(Body, Pixel(dark.Icons[BrandIconKind.App512], 100, 140));
        Assert.Equal(BrandIconComposer.LightTile, Pixel(dark.Icons[BrandIconKind.App512], 256, 140));

        // Белая «Н» — на тёмную плитку: без этого шага она попала бы на «почти белый фон», то есть
        // на белую.
        Assert.Equal(BrandIconComposer.DarkTile, Compose(LetterH(SKColors.White)).Tile);
    }

    public static TheoryData<string, byte[]> ЗнакиБезФона => new()
    {
        // Рамка с прозрачной серединой и знаком того же цвета внутри.
        { "рамка", Shape(Body, [(0f, 0f, 1f, 0.1f), (0f, 0.9f, 1f, 0.1f), (0f, 0f, 0.1f, 1f), (0.9f, 0f, 0.1f, 1f), (0.4f, 0.4f, 0.2f, 0.2f)]) },
        // Плашка с буквой-вырезом: залита почти вся, но вырез — и есть знак.
        { "плашка с вырезом", Shape(Body, [(0f, 0f, 1f, 1f)], holes: [(0.3f, 0.3f, 0.4f, 0.4f)]) },
    };

    [Theory]
    [MemberData(nameof(ЗнакиБезФона))]
    public void Рамка_и_плашка_с_вырезом_не_дают_плитку_своего_цвета(string what, byte[] logo)
    {
        var set = Compose(logo);

        Assert.True(BrandIconComposer.LightTile == set.Tile, $"{what}: плитка {set.Tile} — цвета самого знака");
    }

    /// <summary>
    /// Залитый прямоугольник с РАЗНЫМИ углами — тоже не «свой фон»: продолжать нечего, цвета два.
    /// Плитка «среднего по углам» цвета не совпала бы ни с одним краем логотипа.
    /// </summary>
    [Fact]
    public void Залитый_логотип_с_разными_углами_не_даёт_плитку_среднего_цвета()
    {
        var twoTone = Shape(Body, [(0f, 0f, 0.5f, 1f)], more: [(new SKColor(120, 20, 20), (0.5f, 0f, 0.5f, 1f))]);

        Assert.Equal(BrandIconComposer.LightTile, Compose(twoTone).Tile);
    }

    /// <summary>
    /// Отпечаток набора следует за содержимым значков: он идёт в метку версии и в тег ответа, и по
    /// нему браузер узнаёт, что картинка другая, даже когда логотип тот же.
    /// </summary>
    [Fact]
    public void Отпечаток_набора_один_у_одинаковых_значков_и_разный_у_разных()
    {
        Assert.Equal(Compose(Logo(800, 200)).Stamp, Compose(Logo(800, 200)).Stamp);
        Assert.NotEqual(Compose(Logo(800, 200)).Stamp, Compose(Logo(800, 200, body: SKColors.White)).Stamp);
        Assert.Matches("^[0-9a-f]{8}$", Compose(Logo(800, 200)).Stamp);
    }

    /// <summary>Почти белый фон (сжатие JPEG) — чисто белая плитка, а не «почти».</summary>
    [Fact]
    public void Почти_белый_фон_даёт_чисто_белую_плитку()
    {
        var set = Compose(Logo(800, 200, background: new SKColor(251, 250, 252)));

        Assert.Equal(BrandIconComposer.LightTile, set.Tile);
    }

    /// <summary>
    /// Значки приложения и вкладки — плитка со скруглением и прозрачными углами, как у стандартных;
    /// значок iPhone и маскируемый — во весь квадрат: прозрачность iOS заливает чёрным.
    /// </summary>
    [Fact]
    public void Углы_прозрачны_только_у_скруглённой_плитки()
    {
        var set = Compose(Logo(800, 200));

        Assert.Equal(0, Pixel(set.Icons[BrandIconKind.App192], 0, 0).Alpha);
        Assert.Equal(0, Pixel(set.Icons[BrandIconKind.Tab], 0, 0).Alpha);
        Assert.Equal(255, Pixel(set.Icons[BrandIconKind.Apple], 0, 0).Alpha);
        Assert.Equal(255, Pixel(set.Icons[BrandIconKind.Maskable512], 0, 0).Alpha);
    }

    [Fact]
    public void Пропорции_видимой_части_названы()
    {
        Assert.Equal(4.0, Compose(Logo(800, 200)).Aspect, 2);
        Assert.Equal(0.333, Compose(Logo(200, 600)).Aspect, 2);
    }

    /// <summary>Пустая картинка значков не даёт — и это отказ, а не пять пустых плиток.</summary>
    [Fact]
    public void Картинка_без_единой_видимой_точки_значков_не_даёт()
    {
        using var empty = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul));
        empty.Erase(SKColors.Transparent);
        using var image = SKImage.FromBitmap(empty);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        Assert.Null(BrandIconComposer.Compose(data.ToArray()));
        Assert.Null(BrandIconComposer.Compose("не картинка"u8.ToArray()));
    }
}
