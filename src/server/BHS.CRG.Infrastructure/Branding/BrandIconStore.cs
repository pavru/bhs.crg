using BHS.CRG.Application.Branding;
using BHS.CRG.Infrastructure.Generation;
using Microsoft.Extensions.Logging;

namespace BHS.CRG.Infrastructure.Branding;

/// <summary>Итог построения значков: набор либо причина отказа (<see cref="BrandIconProblem" />).</summary>
public sealed record BrandIconBuild(BrandIconSet? Set, string? Problem);

/// <summary>
/// Значки, построенные из действующего логотипа (issue #1315). Одна на процесс.
///
/// <para><b>Значки — производное, и живут они в памяти.</b> Ни таблицы, ни файлов в хранилище у них
/// нет: строятся за доли секунды, весят килобайты и однозначно следуют из логотипа. Ключ — логотип и
/// его версия, поэтому устаревший набор отдать нечем, а «при первом обращении после перезапуска или
/// восстановления копии» получается само — без особого случая в коде и без строки в резервной
/// копии.</para>
///
/// <para>⚠️ Отсюда же закрыты все двери, которыми логотип меняется мимо службы оформления: экран
/// ассетов шаблонов (замена и удаление файла) и восстановление копии. Сообщать сюда о перемене
/// никому не нужно — у нового файла новый ключ.</para>
///
/// <para>Ячейка одна: значки нужны только от действующего логотипа, а набор от прежнего — мусор.
/// Строит один поток, остальные ждут: адрес значка открыт до входа, и пачка первых обращений после
/// перезапуска не должна обернуться пачкой процессов Typst.</para>
/// </summary>
public sealed class BrandIconStore(ILogoRasterizer rasterizer, ILogger<BrandIconStore> logger)
{
    /// <summary>
    /// Через сколько повторять построение после сбоя сервера. Отказ из-за самого файла не
    /// повторяется вовсе — тот же файл даст тот же отказ; повторит его новая загрузка (новый ключ).
    /// А сбой сервера проходит сам, и без повтора значки остались бы стандартными до перезапуска.
    /// </summary>
    public TimeSpan RetryAfterFailure { get; init; } = TimeSpan.FromMinutes(1);

    private sealed record Slot(string Key, BrandIconBuild Build, long RetryAt);

    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile Slot? slot;

    /// <param name="key">Логотип и его версия. Другой ключ — другой логотип.</param>
    /// <param name="readLogo">Содержимое логотипа; <c>null</c> — файл не читается из хранилища.</param>
    public async Task<BrandIconBuild> EnsureAsync(
        string key, Func<CancellationToken, Task<byte[]?>> readLogo, CancellationToken ct)
    {
        if (Fresh(key) is { } ready) return ready;

        await gate.WaitAsync(ct);
        try
        {
            if (Fresh(key) is { } built) return built;

            var build = await BuildAsync(readLogo, ct);
            var retryAt = build.Problem == BrandIconProblem.Builder
                ? Environment.TickCount64 + (long)RetryAfterFailure.TotalMilliseconds
                : long.MaxValue;
            slot = new Slot(key, build, retryAt);
            return build;
        }
        finally
        {
            gate.Release();
        }
    }

    private BrandIconBuild? Fresh(string key) =>
        slot is { } s && s.Key == key && Environment.TickCount64 < s.RetryAt ? s.Build : null;

    private async Task<BrandIconBuild> BuildAsync(Func<CancellationToken, Task<byte[]?>> readLogo, CancellationToken ct)
    {
        try
        {
            if (await readLogo(ct) is not { } logo) return new(null, BrandIconProblem.Builder);

            // Предел в байтах размеров не ограничивает: PNG на пять мегабайт разворачивается в памяти в
            // гигабайты, а строится значок по запросу без входа.
            if (ImageDownscaler.PixelCountExceeded(logo) is { } tooLarge)
            {
                logger.LogWarning("Значки из логотипа не построены: {Reason}", tooLarge);
                return new(null, BrandIconProblem.Size);
            }

            var raster = await rasterizer.RasterizeAsync(logo, ct);
            if (raster.Png is null) return new(null, raster.Problem ?? BrandIconProblem.Builder);

            if (BrandIconComposer.Compose(raster.Png) is not { } set)
            {
                logger.LogWarning("Значки из логотипа не построены: в картинке нет ни одной видимой точки");
                return new(null, BrandIconProblem.File);
            }

            return new(set, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Значок стоит на странице входа: отказ построения не должен стать отказом страницы.
            // Наружу — состояние «не построены», причина — в журнал.
            logger.LogWarning(ex, "Значки из логотипа не построены: сбой построителя");
            return new(null, BrandIconProblem.Builder);
        }
    }
}
