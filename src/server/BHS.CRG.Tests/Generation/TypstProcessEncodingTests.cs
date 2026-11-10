using System.Text;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Generation;

/// <summary>
/// Вывод Typst читается как UTF-8 — независимо от кодовой страницы консоли сервера.
///
/// <para>Typst пишет stdout и stderr в UTF-8 всегда. А <c>Process</c> без явной кодировки читает
/// перенаправленный поток в кодировке консоли: на Linux это UTF-8, и в поставке беды не видно, а на
/// Windows с русской локалью — OEM 866. Кириллица в отказе компилятора (текст <c>panic</c> из
/// шаблона, имя поля) приезжала в отказ предпросмотра кракозябрами «╨┐╤А╨╛…» — найдено живой
/// проверкой отказа предпросмотра на дев-стенде (PR #1880).</para>
///
/// <para>⚠️ Живой прогон один поймать откат НЕ может: в CI (Linux) поток и без правки читается как
/// UTF-8, а на Windows искажение проявляется лишь при консоли у процесса тестов. Поэтому сторожей
/// два, и срабатывают они везде: фабрика запуска называет кодировку сама, и мимо фабрики Typst не
/// запускает никто. Живой прогон — проверка всей дороги до текста отказа.</para>
/// </summary>
public class TypstProcessEncodingTests
{
    [Fact]
    public void StartInfo_ReadsBothStreamsAsUtf8()
    {
        var psi = TypstProcess.StartInfo(Path.GetTempPath(), "compile", "template.typ");

        Assert.Equal(Encoding.UTF8.WebName, psi.StandardOutputEncoding?.WebName);
        Assert.Equal(Encoding.UTF8.WebName, psi.StandardErrorEncoding?.WebName);
        Assert.True(psi.RedirectStandardOutput && psi.RedirectStandardError);
    }

    /// <summary>
    /// Запуск Typst, собранный руками мимо фабрики, снова читал бы поток в кодировке консоли — а
    /// в CI на Linux это не видно ничем. Признак такого запуска — путь к компилятору вне фабрики.
    /// </summary>
    [Fact]
    public void TypstIsLaunchedOnlyThroughStartInfo()
    {
        // Собран из nameof, а не записан строкой: иначе сторож нашёл бы сам себя.
        var handBuilt = new System.Text.RegularExpressions.Regex(
            $@"(FileName\s*=|ProcessStartInfo\s*\()\s*({nameof(TypstProcess)}\.)?{nameof(TypstProcess.ExecutablePath)}\b");
        // Фабрику узнаём по имени файла, а не по пути к проекту: обход и так идёт по всему src/server,
        // модули включительно.
        var factory = Path.Combine("Generation", "TypstProcess.cs");
        var offenders = Directory
            .EnumerateFiles(RepoPaths.File("src", "server"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.EndsWith(Path.DirectorySeparatorChar + factory, StringComparison.OrdinalIgnoreCase))
            .Where(f => handBuilt.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(RepoPaths.Root, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Typst запускается мимо TypstProcess.StartInfo — без кодировки UTF-8 у потоков: " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// <c>RunAsync</c> общий и принимает любой запуск (тесты срока гоняют им оболочку), поэтому
    /// названную вызывающим кодировку не перетирает: у не-Typst процесса она своя.
    /// </summary>
    [Fact]
    public async Task RunAsync_KeepsCallerEncoding()
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardErrorEncoding = Encoding.Latin1,
        };
        foreach (var a in OperatingSystem.IsWindows() ? new[] { "/c", "exit 0" } : ["-c", "exit 0"])
            psi.ArgumentList.Add(a);

        await TypstProcess.RunAsync(psi, CancellationToken.None, TimeSpan.FromSeconds(30));

        Assert.Same(Encoding.Latin1, psi.StandardErrorEncoding);
    }

    /// <summary>
    /// Вся дорога на настоящем Typst: отказ <c>panic</c> с кириллицей — в stderr и в тексте, который
    /// видит автор шаблона. Провайдер кодовых страниц регистрируем сами: без него <c>Process</c> не
    /// получает 866 и молча берёт UTF-8, а регистрируют его другие классы набора — и исход на Windows
    /// зависел бы от порядка тестов.
    /// </summary>
    [Fact]
    public async Task CyrillicInCompilerRefusal_ReachesUserText()
    {
        TypstRequired.Ensure();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var dir = Path.Combine(Path.GetTempPath(), "typst-encoding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "template.typ"), "#panic(\"проверка кодировки\")\n");

            var psi = TypstProcess.StartInfo(dir,
                "compile", "template.typ", "output.pdf", "--diagnostic-format", "short", "--root", dir);
            var (exitCode, stderr) = await TypstProcess.RunAsync(psi, CancellationToken.None);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("проверка кодировки", stderr);

            var refusal = TypstCompileFailure.TryDescribe(stderr, dir);
            Assert.NotNull(refusal);
            Assert.Contains("проверка кодировки", refusal.Message);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
