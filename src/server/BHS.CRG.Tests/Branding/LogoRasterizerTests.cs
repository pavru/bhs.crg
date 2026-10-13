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

        var result = await rasterizer.RasterizeAsync(Logo(800, 200), default);

        Assert.Equal((null, BrandIconProblem.Timeout), (result.Png, result.Problem));
    }

    [Fact]
    public async Task Обычный_логотип_растрируется_в_квадрат()
    {
        var rasterizer = new TypstLogoRasterizer(NullLogger<TypstLogoRasterizer>.Instance);

        var result = await rasterizer.RasterizeAsync(Logo(800, 200), default);

        Assert.Null(result.Problem);
        Assert.Equal((TypstLogoRasterizer.Side, TypstLogoRasterizer.Side), Size(result.Png!));
    }
}
