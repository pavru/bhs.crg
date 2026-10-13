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
    Task<RasterizedLogo> RasterizeAsync(byte[] logo, CancellationToken ct);
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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string LogoFile = "logo";
    private const string PageFile = "logo.typ";
    private const string OutputFile = "logo.png";

    /// <summary>
    /// Страница — квадрат в пунктах; при <c>--ppi 72</c> пункт равен точке. Логотип читается
    /// БАЙТАМИ, без расширения: формат Typst определяет по содержимому. Расширению верить нельзя —
    /// загрузка сверяет только его, а через экран ассетов шаблонов логотип заменяется файлом с любым
    /// именем; JPEG под именем «.png» дал бы отказ там, где браузер картинку показывает.
    /// </summary>
    private static readonly string Page =
        $"#set page(width: {Side}pt, height: {Side}pt, margin: 0pt, fill: none)\n" +
        $"#place(center + horizon, image(read(\"{LogoFile}\", encoding: none), width: 100%, height: 100%, fit: \"contain\"))\n";

    public async Task<RasterizedLogo> RasterizeAsync(byte[] logo, CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "brand-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, LogoFile), logo, ct);
            await File.WriteAllTextAsync(Path.Combine(dir, PageFile), Page, ct);

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
            catch (ConflictException ex)
            {
                // Typst недоступен либо не уложился в срок. Тексты этих отказов написаны про шаблон
                // печатной формы («бесконечный цикл в шаблоне») — наружу их не отдаём, только в журнал.
                logger.LogWarning(ex, "Значки из логотипа не построены: Typst не запустился или не уложился в срок");
                return new(null, BrandIconProblem.Builder);
            }

            var output = Path.Combine(dir, OutputFile);
            // Судим по коду возврата и по файлу: предупреждения Typst тоже пишет в stderr, при коде 0.
            if (exitCode != 0)
            {
                // В журнал — целиком: там пути временного каталога, наружу им нельзя.
                logger.LogWarning("Typst не разобрал логотип как изображение: {StdErr}", stderr);
                return new(null, BrandIconProblem.File);
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
