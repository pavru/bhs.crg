using System.Text;
using BHS.CRG.Application.Branding;
using BHS.CRG.Infrastructure.Branding;
using Microsoft.Extensions.Logging.Abstractions;
using static BHS.CRG.Tests.Branding.BrandIconSamples;

namespace BHS.CRG.Tests.Branding;

/// <summary>
/// Растеризатор логотипа (issue #1315) — на НАСТОЯЩЕМ Typst: здесь проверяется, чем он отвечает на
/// отказы самого запуска. Что выходит из каждого формата, держат интеграционные тесты значков.
/// </summary>
public class LogoRasterizerTests
{
    public LogoRasterizerTests() => TypstRequired.Ensure();

    private static TypstLogoRasterizer Rasterizer() => new(NullLogger<TypstLogoRasterizer>.Instance);

    /// <summary>
    /// Не уложился в срок — своя причина, а не «сбой сервера». В срок может не уложиться и сам файл
    /// (тяжёлый SVG), и по одному отказу это не отличить от занятого сервера; совет «причина на
    /// сервере, а не в файле» был бы здесь неправдой.
    /// </summary>
    [Fact]
    public async Task Истёкший_срок_назван_своей_причиной()
    {
        var rasterizer = new TypstLogoRasterizer(NullLogger<TypstLogoRasterizer>.Instance)
        {
            // Срок, в который не уложится никакой запуск: один старт процесса дольше.
            Timeout = TimeSpan.FromMilliseconds(1),
        };

        var result = await rasterizer.RasterizeAsync(Logo(800, 200), null, default);

        Assert.Equal((null, BrandIconProblem.Timeout), (result.Png, result.Problem));
    }

    [Fact]
    public async Task Обычный_логотип_растрируется_в_квадрат()
    {
        var result = await Rasterizer().RasterizeAsync(Logo(800, 200), null, default);

        Assert.Null(result.Problem);
        Assert.Equal((TypstLogoRasterizer.Side, TypstLogoRasterizer.Side), Size(result.Png!));
    }

    /// <summary>
    /// SVG с длинным прологом — комментарий редактора, лицензия. Typst узнаёт SVG по строке
    /// пространства имён в первых двух килобайтах и без подсказки такой файл отвергал: «unknown
    /// image format», значков нет навсегда — при том, что браузер показывает его в шапке.
    /// </summary>
    [Fact]
    public async Task Svg_с_длинным_прологом_растрируется()
    {
        var svg = Encoding.UTF8.GetString(Svg(800, 200));
        var prolog = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!-- " + new string('x', 3000) + " -->\n";

        var result = await Rasterizer().RasterizeAsync(Encoding.UTF8.GetBytes(prolog + svg), null, default);

        Assert.Null(result.Problem);
        Assert.Equal((TypstLogoRasterizer.Side, TypstLogoRasterizer.Side), Size(result.Png!));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"/>", true)]
    [InlineData("﻿ \r\n\t<?xml version=\"1.0\"?><svg/>", true)]
    [InlineData("GIF89a<", false)]
    [InlineData("", false)]
    public void Вектор_узнаётся_по_первому_значащему_знаку(string content, bool vector) =>
        Assert.Equal(vector, LogoFormat.IsVector(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public void Растровые_образцы_вектором_не_считаются()
    {
        Assert.False(LogoFormat.IsVector(Logo(80, 20)));
        Assert.False(LogoFormat.IsVector(Logo(80, 20, SkiaSharp.SKEncodedImageFormat.Jpeg, background: SkiaSharp.SKColors.White)));
        Assert.False(LogoFormat.IsVector(Logo(80, 20, SkiaSharp.SKEncodedImageFormat.Webp)));
    }

    /// <summary>
    /// Отказ файлу — только код 1: так Typst отвечает, разобрав запуск. Убитый по памяти (137) или
    /// упавший паникой (101) процесс — сбой сервера: он проходит сам, а «файл» не повторяется
    /// никогда и остался бы до перезапуска.
    /// </summary>
    [Theory]
    [InlineData(1, BrandIconProblem.File)]
    [InlineData(101, BrandIconProblem.Builder)]
    [InlineData(137, BrandIconProblem.Builder)]
    [InlineData(-1, BrandIconProblem.Builder)]
    public void Чей_отказ_решает_код_возврата(int exitCode, string problem) =>
        Assert.Equal(problem, TypstLogoRasterizer.ProblemOf(exitCode));

    /// <summary>
    /// Знак в восьмой части холста: первый проход даёт его в сотню точек, второй — только его, и
    /// уже в нужном размере. Проверяется на настоящем Typst — страницу со сдвигом холста строит он.
    /// </summary>
    [Fact]
    public async Task Второй_проход_растрирует_видимую_часть_крупнее()
    {
        var svg = Encoding.UTF8.GetBytes(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1000 1000\">"
            + "<rect x=\"440\" y=\"450\" width=\"120\" height=\"100\" fill=\"#143c8c\"/>"
            + "<rect x=\"440\" y=\"450\" width=\"10\" height=\"100\" fill=\"#dc0000\"/>"
            + "<rect x=\"550\" y=\"450\" width=\"10\" height=\"100\" fill=\"#dc0000\"/></svg>");
        var rasterizer = Rasterizer();

        var first = await rasterizer.RasterizeAsync(svg, null, default);
        var view = BrandIconComposer.CloserView(first.Png!);
        Assert.NotNull(view);
        var closer = await rasterizer.RasterizeAsync(svg, view, default);

        Assert.Null(closer.Problem);
        // Знак целиком и вчетверо крупнее: 120 единиц холста — 123 точки первого растра, 491 — второго.
        var marks = Marks(closer.Png!);
        Assert.InRange(marks.Width, 486, 496);
        Assert.InRange(marks.Height, 404, 414);
        // Точек теперь хватает: третьего прохода сборщик не просит.
        Assert.Null(BrandIconComposer.CloserView(closer.Png!));
        var set = BrandIconComposer.Compose(closer.Png!);
        Assert.Equal(1.2, set!.Aspect, 1);
    }
}
