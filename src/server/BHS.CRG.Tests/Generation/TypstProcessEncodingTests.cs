using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Tests.Branding;

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
/// <para>Нужен настоящий Typst: подмена его командой оболочки проверила бы кодировку оболочки, а не
/// ту, в которой пишет компилятор.</para>
/// </summary>
public class TypstProcessEncodingTests
{
    public TypstProcessEncodingTests() => TypstRequired.Ensure();

    [Fact]
    public async Task CyrillicInCompilerRefusal_ReachesUserText()
    {
        var dir = Path.Combine(Path.GetTempPath(), "typst-encoding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "template.typ"), "#panic(\"проверка кодировки\")\n");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = TypstProcess.ExecutablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = dir,
            };
            foreach (var a in new[] { "compile", "template.typ", "output.pdf", "--diagnostic-format", "short", "--root", dir })
                psi.ArgumentList.Add(a);

            var (exitCode, stderr) = await TypstProcess.RunAsync(psi, CancellationToken.None);

            Assert.NotEqual(0, exitCode);
            Assert.Contains("проверка кодировки", stderr);

            // И дальше по дороге к пользователю: разобранный отказ — тот, что видит автор шаблона.
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
