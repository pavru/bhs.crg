using System.Diagnostics;
using BHS.CRG.Infrastructure.Generation;

namespace BHS.CRG.Tests.Support;

/// <summary>
/// Предпосылка тестов, которым нужен настоящий Typst: значки из логотипа (issue #1315), кодировка
/// вывода компилятора. Без этой проверки его отсутствие выглядело как полтора десятка несвязанных
/// падений — «Expected: custom, Actual: failed», несовпавшие размеры, — а про Typst говорило одно
/// предупреждение в журнале (ревью PR #1320).
/// </summary>
public static class TypstRequired
{
    private static readonly Lazy<string?> Missing = new(() =>
    {
        try
        {
            using var process = Process.Start(TypstProcess.StartInfo(Environment.CurrentDirectory, "--version"));
            if (process is null) return "процесс не запустился";
            process.WaitForExit(TimeSpan.FromSeconds(30));
            return process.HasExited && process.ExitCode == 0 ? null : "команда --version не ответила кодом 0";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ex.Message;
        }
    });

    /// <summary>Падает с названной причиной, если Typst не запускается.</summary>
    public static void Ensure()
    {
        if (Missing.Value is { } reason)
            throw new Xunit.Sdk.XunitException(
                $"Этому тесту нужен настоящий Typst, а «{TypstProcess.ExecutablePath}» не запускается ({reason}). " +
                "Поставьте Typst CLI в PATH либо назовите путь переменной TYPST_PATH.");
    }
}
