using System.Diagnostics;
using BHS.CRG.Application.Branding;
using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Branding;

/// <summary>Логотип в точках — либо причина, по которой его не удалось в них превратить.</summary>
/// <param name="Png">Квадратная картинка с прозрачным фоном: логотип вписан целиком, по центру.</param>
/// <param name="Problem">Одно из <see cref="BrandIconProblem" />; <c>null</c> при успехе.</param>
public sealed record RasterizedLogo(byte[]? Png, string? Problem);

/// <summary>
/// Превращает логотип в точки. Отдельным швом — ради тестов: порядок «построить один раз, не строить
/// повторно, повторить после сбоя» проверяется на подставном растеризаторе, без процесса Typst.
/// </summary>
public interface ILogoRasterizer
{
    /// <param name="view">
    /// Какую часть холста растрировать и во сколько раз крупнее; <c>null</c> — холст целиком, в
    /// квадрат <see cref="TypstLogoRasterizer.Side" />.
    /// </param>
    Task<RasterizedLogo> RasterizeAsync(byte[] logo, LogoView? view, CancellationToken ct);
}

/// <summary>
/// Часть холста логотипа, растрируемая крупнее (второй проход, ревью PR #1320). Координаты — в
/// точках первого растра (квадрат <see cref="TypstLogoRasterizer.Side" />).
///
/// <para>Зачем. Первый растр — весь холст файла вместе с пустыми полями. У знака, занимающего
/// восьмую часть холста, видимая часть выходит в сотню точек, а значку нужно почти четыреста:
/// векторный логотип растягивался бы втрое и выходил мутным. Вторым проходом Typst рисует только
/// найденный прямоугольник — страница размером с него, а не с увеличенный холст.</para>
/// </summary>
public sealed record LogoView(int Left, int Top, int Width, int Height, double Scale);

/// <summary>Что за файл лежит под именем логотипа — по содержимому, а не по расширению.</summary>
public static class LogoFormat
{
    /// <summary>
    /// Похоже ли содержимое на SVG: первый значащий знак — «&lt;». Три растровых формата начинаются
    /// с двоичной подписи и под это не попадают.
    /// </summary>
    public static bool IsVector(ReadOnlySpan<byte> logo)
    {
        if (logo.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) logo = logo[3..];
        foreach (var b in logo)
        {
            if (b is 0x20 or 0x09 or 0x0D or 0x0A) continue;
            return b == (byte)'<';
        }

        return false;
    }
}

/// <summary>
/// Растрирует логотип Typst CLI (issue #1315, решено пробой 13.10.2026): страница-квадрат с
/// <c>image(…, fit: "contain")</c> компилируется в PNG.
///
/// <para>Почему Typst, а не библиотека картинок. Логотип принимается в четырёх форматах, один из
/// них векторный; SkiaSharp SVG не читает, а Typst уже стоит в образе и растрирует все четыре
/// одним приёмом. Тем же Typst логотип попадает в печатные формы — значок и печать видят файл
/// одинаково.</para>
///
/// <para>⚠️ Каталог прогона содержит ТОЛЬКО логотип и страницу. SVG — чужой файл: ссылку в сеть,
/// абсолютный путь и выход за корень Typst отвергает сам (отказом всей компиляции), а вот файл
/// РЯДОМ с логотипом прочёл бы и нарисовал — в значке, который отдаётся без входа.</para>
/// </summary>
public sealed class TypstLogoRasterizer(ILogger<TypstLogoRasterizer> logger) : ILogoRasterizer
{
    /// <summary>
    /// Сторона растра. С запасом над самым крупным значком (512): прозрачные поля логотипа потом
    /// срезаются, и вписывается то, что осталось.
    /// </summary>
    public const int Side = 1024;

    /// <summary>Срок на запуск. Логотип растрируется за доли секунды; десять секунд — уже «не закончится».</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    private const string LogoFile = "logo";
    private const string PageFile = "logo.typ";
    private const string OutputFile = "logo.png";

    /// <summary>
    /// Страница в пунктах; при <c>--ppi 72</c> пункт равен точке. Логотип читается БАЙТАМИ, без
    /// расширения: формат Typst определяет по содержимому. Расширению верить нельзя — загрузка
    /// сверяет только его, а через экран ассетов шаблонов логотип заменяется файлом с любым именем;
    /// JPEG под именем «.png» дал бы отказ там, где браузер картинку показывает.
    ///
    /// <para>⚠️ SVG называем САМИ. Typst узнаёт его по строке пространства имён в первых двух
    /// килобайтах файла; SVG с длинным прологом (комментарий редактора, лицензия, DOCTYPE) он
    /// отвергал как «unknown image format» — при том, что браузер такой файл показывает в шапке, а
    /// печатная форма ставит по расширению (ревью PR #1320, проверено на Typst 0.15.1).</para>
    ///
    /// <para>Холст — квадрат со стороной <see cref="Side" />, логотип вписан в него целиком. Без
    /// <paramref name="view" /> страница — весь холст; с ним — только названный прямоугольник,
    /// увеличенный: холст сдвинут так, что прямоугольник встаёт в начало страницы, а остальное
    /// остаётся за её краями и не рисуется.</para>
    /// </summary>
    public static string PageOf(bool vector, LogoView? view)
    {
        var format = vector ? ", format: \"svg\"" : "";
        var scale = view?.Scale ?? 1;
        static string Pt(double value) => FormattableString.Invariant($"{value:0.###}pt");

        var (width, height) = view is null ? (Side, Side) : (view.Width * scale, view.Height * scale);
        var (dx, dy) = view is null ? (0d, 0d) : (-view.Left * scale, -view.Top * scale);
        return
            $"#set page(width: {Pt(Math.Ceiling(width))}, height: {Pt(Math.Ceiling(height))}, margin: 0pt, fill: none)\n" +
            $"#place(top + left, dx: {Pt(dx)}, dy: {Pt(dy)}, image(read(\"{LogoFile}\", encoding: none){format}, " +
            $"width: {Pt(Side * scale)}, height: {Pt(Side * scale)}, fit: \"contain\"))\n";
    }

    /// <summary>
    /// Чей отказ — по коду возврата. Единица — Typst разобрал запуск и отказал самому файлу: тот же
    /// файл даст тот же отказ, и повторять его незачем. Любой другой код — процесс до ответа не
    /// дожил: убит по памяти (137), упал паникой (101). Это сбой сервера, он проходит сам — а
    /// записанный «файлом», он остался бы до перезапуска, с советом пересохранить исправный логотип.
    /// </summary>
    public static string ProblemOf(int exitCode) =>
        exitCode == 1 ? BrandIconProblem.File : BrandIconProblem.Builder;

    public async Task<RasterizedLogo> RasterizeAsync(byte[] logo, LogoView? view, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "brand-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, LogoFile), logo, ct);
            await File.WriteAllTextAsync(Path.Combine(dir, PageFile), PageOf(LogoFormat.IsVector(logo), view), ct);

            var psi = new ProcessStartInfo
            {
                FileName = TypstProcess.ExecutablePath,
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in (string[])
                     ["compile", PageFile, OutputFile, "--format", "png", "--ppi", "72", "--root", dir,
                         "--diagnostic-format", "short"])
                psi.ArgumentList.Add(arg);

            int exitCode;
            string stderr;
            try
            {
                (exitCode, stderr) = await TypstProcess.RunAsync(psi, ct, Timeout);
            }
            // Тексты обоих отказов написаны про шаблон печатной формы («бесконечный цикл в шаблоне»)
            // — наружу их не отдаём, только в журнал.
            catch (TypstTimeoutException ex)
            {
                // Своей причиной, а не «сбоем сервера»: в срок может не уложиться и сам файл.
                logger.LogWarning(ex, "Значки из логотипа не построены: Typst не уложился в срок");
                return new(null, BrandIconProblem.Timeout);
            }
            catch (ConflictException ex)
            {
                logger.LogWarning(ex, "Значки из логотипа не построены: Typst не запустился");
                return new(null, BrandIconProblem.Builder);
            }

            var output = Path.Combine(dir, OutputFile);
            // Судим по коду возврата и по файлу: предупреждения Typst тоже пишет в stderr, при коде 0.
            if (exitCode != 0)
            {
                // В журнал — целиком: там пути временного каталога, наружу им нельзя.
                logger.LogWarning("Typst не построил картинку логотипа, код {ExitCode}: {StdErr}", exitCode, stderr);
                return new(null, ProblemOf(exitCode));
            }

            if (!File.Exists(output))
            {
                logger.LogWarning("Typst завершился без ошибки, но картинки логотипа не оставил: {StdErr}", stderr);
                return new(null, BrandIconProblem.Builder);
            }

            return new(await File.ReadAllBytesAsync(output, ct), null);
        }
        finally
        {
            // Удаление — как у генерации: по возможности. Оставшийся каталог — мусор во временной
            // папке, а не поломка.
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Временный каталог значков не удалён: {Dir}", dir);
            }
        }
    }
}
