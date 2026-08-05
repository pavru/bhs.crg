using System.Diagnostics;

namespace BHS.CRG.Infrastructure.Generation;

/// <summary>
/// Запуск Typst CLI с обязательным сроком и гарантированным завершением процесса.
/// </summary>
/// <remarks>
/// <para>
/// Раньше все три места (генерация PDF, проверка синтаксиса шаблона, проверка библиотеки) ждали
/// процесс через <c>WaitForExitAsync(ct)</c> без срока и без <c>Kill</c>. Отсюда две беды, обе
/// достижимые обычным пользователем: шаблон с бесконечным циклом занимал ядро навсегда, а обрыв
/// соединения (закрыли вкладку предпросмотра) оставлял процесс жить — токен отменял ОЖИДАНИЕ, но не
/// сам процесс. Вызывающий тем временем шёл в <c>finally</c> удалять временную папку, которую этот
/// же процесс держал.
/// </para>
/// <para>
/// Здесь и срок, и завершение дерева процессов в <c>finally</c> — до того, как вызывающий возьмётся
/// за папку.
/// </para>
/// </remarks>
public static class TypstProcess
{
    /// <summary>
    /// Срок на один запуск. Взят с большим запасом: тяжёлый комплект со сканами верстается единицы
    /// секунд, а всё, что не уложилось в минуту, — это уже не «медленно», а «не закончится».
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Сколько ждём фактической смерти процесса после Kill, прежде чем отпустить папку.</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Запускает процесс, дожидается завершения и возвращает код возврата вместе с stderr.
    /// </summary>
    /// <exception cref="TypstTimeoutException">Процесс не уложился в срок.</exception>
    public static async Task<(int ExitCode, string StdErr)> RunAsync(
        ProcessStartInfo psi, CancellationToken ct, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Не удалось запустить Typst");

        // Читать надо ОБА потока и начинать до ожидания. Буфер канала невелик: процесс, написавший
        // в невычитываемый stdout больше буфера, блокируется на записи и не завершается никогда —
        // до этой правки такое висло бесконечно, а с одним лишь сроком выглядело бы как «шаблон
        // зациклился», хотя он просто разговорчивый.
        //
        // Читаем БЕЗ токена: чтение закончится само, когда процесс умрёт и канал закроется, — а
        // умереть ему поможет Kill в finally. С токеном отмена рвала бы чтение, оставляя задачу с
        // необработанным исключением, и до текста stderr мы бы уже не добрались.
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return (process.ExitCode, await stdErrTask);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Отменили не снаружи, значит истёк наш срок.
            throw new TypstTimeoutException(
                $"Вёрстка не завершилась за {limit.TotalSeconds:0} с и была остановлена. " +
                "Обычно это бесконечный цикл или рекурсия в шаблоне.");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    // Именно деревом: Typst сам подпроцессов не плодит, но убивать надо то, что
                    // держит рабочую папку, а не только то, что мы видим.
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit((int)KillGrace.TotalMilliseconds);
                }
            }
            catch { /* процесс уже умер сам — это и требовалось */ }

            // Задачи чтения могли не завершиться (или завершиться отказом) — наблюдаем их, чтобы
            // необработанное исключение не всплыло позже в финализаторе, уже без всякой связи с
            // этим запуском.
            Observe(stdOutTask);
            Observe(stdErrTask);
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
}

/// <summary>Вёрстка не уложилась в отведённый срок и была остановлена.</summary>
public class TypstTimeoutException(string message) : Exception(message);
