using BHS.CRG.Application.Branding;
using BHS.CRG.Infrastructure.Generation;
using BHS.CRG.Infrastructure.Http;
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
    /// Через сколько повторять построение после отказа, который проходит сам: сбой сервера или
    /// истёкший срок. Отказ из-за самого файла не повторяется вовсе — тот же файл даст тот же
    /// отказ; повторит его новая загрузка (новый ключ). А сбой сервера проходит сам, и без повтора
    /// значки остались бы стандартными до перезапуска.
    ///
    /// <para>Пауза РАСТЁТ: каждый следующий отказ подряд удваивает её, до
    /// <see cref="RetryCeiling" />. Повтор не бесплатен: это процесс Typst и чтение хранилища, а
    /// запускает его запрос без входа. Ждать его при этом никому не приходится — он идёт в фоне,
    /// см. <see cref="EnsureAsync" />.</para>
    /// </summary>
    public TimeSpan RetryAfterFailure { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Предел растущей паузы между повторами.</summary>
    public TimeSpan RetryCeiling { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Срок на всё построение — чтение логотипа из хранилища и растеризацию.
    ///
    /// <para>⚠️ Построения ждёт <c>GET /api/branding</c> — название в шапке каждого экрана и на
    /// странице входа; до значков этот адрес делал два чтения базы и ни от хранилища, ни от Typst
    /// не зависел. У чтения из хранилища своего срока нет (у клиента — полчаса): хранилище,
    /// которое приняло соединение и молчит, держало бы за общим замком всех, а оборванный запрос не
    /// запоминался бы — следующий в очереди повторял бы то же. Истёкший срок — сбой построителя: он
    /// запоминается, и очередь получает готовый ответ.</para>
    ///
    /// <para>Срок НЕ полагается на то, что чтение отзовётся на отмену. Хранилище отмену слышит и
    /// посреди тела файла (<c>MinIOBlobStorage.DownloadAsync</c>), но читатель логотипа здесь
    /// чужой — его передают снаружи, и за то, что он донесёт токен до конца, этот класс не
    /// ручается. Поэтому чтение ждут не дольше срока и бросают — см. <c>BuildAsync</c>.</para>
    /// </summary>
    public TimeSpan BuildDeadline { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Часы, в миллисекундах; свои — у тестов растущей паузы.</summary>
    public Func<long> Clock { get; init; } = () => Environment.TickCount64;

    /// <param name="Failures">Сколько отказов подряд дал этот логотип — по ним растёт пауза.</param>
    private sealed record Slot(string Key, BrandIconBuild Build, long RetryAt, int Failures);

    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile Slot? slot;

    /// <param name="key">Логотип и его версия. Другой ключ — другой логотип.</param>
    /// <param name="readLogo">Содержимое логотипа; <c>null</c> — файл не читается из хранилища.</param>
    public async Task<BrandIconBuild> EnsureAsync(
        string key, Func<CancellationToken, Task<byte[]?>> readLogo, CancellationToken ct)
    {
        if (Fresh(key) is { } ready) return ready;

        // Повтор после сбоя идёт В ФОНЕ, а спросивший получает запомненный отказ сразу. Ждут
        // построения только те, кому отвечать ещё нечем: первое обращение после перезапуска и
        // загрузка логотипа (у неё новый ключ). Иначе логотип, на котором Typst не укладывается в
        // срок, задерживал бы название в шапке на весь срок при каждом повторе — бессрочно, пусть и
        // всё реже (ревью PR #1320).
        if (slot is { } failed && failed.Key == key)
        {
            RetryInBackground(key, readLogo);
            return failed.Build;
        }

        await gate.WaitAsync(ct);
        try
        {
            if (Fresh(key) is { } built) return built;

            var build = await BuildAsync(readLogo, ct);
            slot = Remembered(key, build);
            return build;
        }
        finally
        {
            gate.Release();
        }
    }

    private int retrying;

    /// <summary>Идущий фоновый повтор; завершённая задача, когда его нет. Тестам — дождаться.</summary>
    public Task Retry { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// ⚠️ Повтор переживает запрос, который его начал: <paramref name="readLogo" /> не вправе
    /// держаться за то, что живёт до конца запроса (контекст базы).
    /// </summary>
    private void RetryInBackground(string key, Func<CancellationToken, Task<byte[]?>> readLogo)
    {
        if (Interlocked.CompareExchange(ref retrying, 1, 0) != 0) return;

        Retry = Task.Run(async () =>
        {
            try
            {
                await gate.WaitAsync();
                try
                {
                    // Пока ждали замок, логотип могли сменить или построить: тогда повторять нечего.
                    if (slot is { } s && s.Key == key && Fresh(key) is null)
                        slot = Remembered(key, await BuildAsync(readLogo, CancellationToken.None));
                }
                finally
                {
                    gate.Release();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Повторное построение значков из логотипа сорвалось");
            }
            finally
            {
                Volatile.Write(ref retrying, 0);
            }
        });
    }

    private BrandIconBuild? Fresh(string key) =>
        slot is { } s && s.Key == key && Clock() < s.RetryAt ? s.Build : null;

    private Slot Remembered(string key, BrandIconBuild build)
    {
        if (build.Problem is not (BrandIconProblem.Builder or BrandIconProblem.Timeout))
            return new Slot(key, build, long.MaxValue, 0);

        var failures = slot is { } s && s.Key == key ? s.Failures + 1 : 1;
        var pause = Math.Min(
            RetryAfterFailure.TotalMilliseconds * Math.Pow(2, failures - 1), RetryCeiling.TotalMilliseconds);
        return new Slot(key, build, Clock() + (long)pause, failures);
    }

    private async Task<BrandIconBuild> BuildAsync(Func<CancellationToken, Task<byte[]?>> readLogo, CancellationToken caller)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        deadline.CancelAfter(BuildDeadline);
        var ct = deadline.Token;
        try
        {
            // WaitAsync, а не голое ожидание: срок обязан истечь, даже если чтение отмену не
            // наблюдает. Чтение из хранилища её наблюдает и обрывается само; брошенным останется
            // только читатель, который токен до хранилища не донёс. Его отказ к тому времени никому
            // не нужен, но оставить его без присмотра нельзя: необработанное исключение всплыло бы
            // позже, уже без связи с этим местом.
            var reading = readLogo(ct);
            _ = reading.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (await reading.WaitAsync(ct) is not { } logo) return new(null, BrandIconProblem.Builder);

            // Предел в байтах размеров не ограничивает: PNG на пять мегабайт разворачивается в памяти в
            // гигабайты, а строится значок по запросу без входа.
            if (ImageDownscaler.PixelCountExceeded(logo) is { } tooLarge)
            {
                logger.LogWarning("Значки из логотипа не построены: {Reason}", tooLarge);
                return new(null, BrandIconProblem.Size);
            }

            var raster = await rasterizer.RasterizeAsync(logo, null, ct);
            if (raster.Png is null) return new(null, raster.Problem ?? BrandIconProblem.Builder);

            // Второй проход — векторному логотипу с пустыми полями: см. LogoView. Не удался —
            // остаёмся с первым растром: значок выйдет мягче, но выйдет.
            if (LogoFormat.IsVector(logo) && BrandIconComposer.CloserView(raster.Png) is { } view
                && (await rasterizer.RasterizeAsync(logo, view, ct)).Png is { } closer)
                raster = new(closer, null);

            if (BrandIconComposer.Compose(raster.Png) is not { } set)
            {
                logger.LogWarning("Значки из логотипа не построены: в картинке нет ни одной видимой точки");
                return new(null, BrandIconProblem.File);
            }

            return new(set, null);
        }
        // Отмену сверяем с токеном ВЫЗЫВАЮЩЕГО, а не со своим сроком. Ушёл человек — это отмена, и
        // запоминать её нельзя: следующему значки нужны. Истёк срок — сбой построителя, и его
        // запоминаем: иначе каждый следующий в очереди ждал бы тот же срок заново.
        catch (Exception ex) when (!HttpFailure.IsUserCancellation(ex, caller))
        {
            // Значок стоит на странице входа: отказ построения не должен стать отказом страницы.
            // Наружу — состояние «не построены», причина — в журнал.
            if (ex is OperationCanceledException)
                logger.LogWarning("Значки из логотипа не построены: не уложились в срок {Deadline}", BuildDeadline);
            else
                logger.LogWarning(ex, "Значки из логотипа не построены: сбой построителя");
            return new(null, BrandIconProblem.Builder);
        }
    }
}
