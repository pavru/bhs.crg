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
