using System.Buffers.Binary;
using System.IO.Hashing;
using BHS.CRG.Application.Branding;
using BHS.CRG.Infrastructure.Branding;
using Microsoft.Extensions.Logging.Abstractions;
using static BHS.CRG.Tests.Branding.BrandIconSamples;

namespace BHS.CRG.Tests.Branding;

/// <summary>
/// Когда значки строятся (issue #1315): один раз на логотип, заново — для нового, повторно — только
/// после сбоя сервера. Растеризатор подставной: проверяется порядок, а не картинка.
///
/// <para>Адрес значка открыт до входа и зовётся каждой страницей. Каждое лишнее построение здесь —
/// процесс Typst по запросу кого угодно.</para>
/// </summary>
public class BrandIconStoreTests
{
    private static readonly byte[] Wide = Logo(800, 200);

    private static Task<byte[]?> Bytes(CancellationToken _) => Task.FromResult<byte[]?>(Wide);

    private static BrandIconStore Store(Counting rasterizer, TimeSpan? retry = null) =>
        retry is { } r
            ? new(rasterizer, NullLogger<BrandIconStore>.Instance) { RetryAfterFailure = r }
            : new(rasterizer, NullLogger<BrandIconStore>.Instance);

    /// <summary>Часы, которые двигает тест: растущую паузу иначе пришлось бы ждать минутами.</summary>
    private sealed class Hands
    {
        public long Now;
    }

    [Fact]
    public async Task Один_логотип_строится_один_раз()
    {
        var rasterizer = new Counting();
        var store = Store(rasterizer);

        var first = await store.EnsureAsync("logo:1", Bytes, default);
        var second = await store.EnsureAsync("logo:1", Bytes, default);

        Assert.NotNull(first.Set);
        Assert.Same(first, second);
        Assert.Equal(1, rasterizer.Calls);
    }

    [Fact]
    public async Task Новый_логотип_строится_заново()
    {
        var rasterizer = new Counting();
        var store = Store(rasterizer);

        await store.EnsureAsync("logo:1", Bytes, default);
        await store.EnsureAsync("logo:2", Bytes, default);

        Assert.Equal(2, rasterizer.Calls);
    }

    /// <summary>
    /// После перезапуска первые обращения приходят пачкой: значок, значок для iPhone, манифест.
    /// Строит один, остальные ждут его результата — а не запускают по своему Typst.
    /// </summary>
    [Fact]
    public async Task Одновременные_обращения_строят_один_раз()
    {
        var rasterizer = new Counting { Delay = TimeSpan.FromMilliseconds(150) };
        var store = Store(rasterizer);

        var builds = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => store.EnsureAsync("logo:1", Bytes, default)));

        Assert.Equal(1, rasterizer.Calls);
        Assert.All(builds, b => Assert.NotNull(b.Set));
    }

    /// <summary>Тот же файл даст тот же отказ — повторять его незачем; повторит новая загрузка.</summary>
    [Fact]
    public async Task Отказ_из_за_файла_не_повторяется()
    {
        var rasterizer = new Counting { Answer = new RasterizedLogo(null, BrandIconProblem.File) };
        var store = Store(rasterizer, retry: TimeSpan.Zero);

        var first = await store.EnsureAsync("logo:1", Bytes, default);
        await store.EnsureAsync("logo:1", Bytes, default);

        Assert.Equal((null, BrandIconProblem.File), (first.Set, first.Problem));
        Assert.Equal(1, rasterizer.Calls);
    }

    /// <summary>
    /// Сбой сервера проходит сам — и без повтора значки остались бы стандартными до перезапуска,
    /// при исправном логотипе.
    /// </summary>
    [Fact]
    public async Task Сбой_сервера_повторяется_и_значки_появляются()
    {
        var rasterizer = new Counting { Answer = new RasterizedLogo(null, BrandIconProblem.Builder) };
        var store = Store(rasterizer, retry: TimeSpan.Zero);

        var failed = await store.EnsureAsync("logo:1", Bytes, default);
        rasterizer.Answer = null;
        var built = await store.EnsureAsync("logo:1", Bytes, default);

        Assert.Equal(BrandIconProblem.Builder, failed.Problem);
        Assert.NotNull(built.Set);
        Assert.Equal(2, rasterizer.Calls);
    }

    /// <summary>
    /// Повтор не бесплатен: пока он идёт, за ним стоят все, кому нужно оформление. Поэтому пауза
    /// растёт — минута, две, четыре… до предела, — а с постоянной минутой логотип, на котором Typst
    /// не укладывается в срок, задерживал бы название в шапке на десять секунд каждую минуту,
    /// бессрочно.
    /// </summary>
    [Theory]
    [InlineData(BrandIconProblem.Builder)]
    [InlineData(BrandIconProblem.Timeout)]
    public async Task Пауза_между_повторами_растёт_с_каждым_отказом_подряд(string problem)
    {
        var hands = new Hands();
        var rasterizer = new Counting { Answer = new RasterizedLogo(null, problem) };
        var store = new BrandIconStore(rasterizer, NullLogger<BrandIconStore>.Instance)
        {
            RetryAfterFailure = TimeSpan.FromMinutes(1), RetryCeiling = TimeSpan.FromMinutes(5),
            Clock = () => hands.Now,
        };

        async Task<int> CallsAfter(TimeSpan passed)
        {
            hands.Now += (long)passed.TotalMilliseconds;
            await store.EnsureAsync("logo:1", Bytes, default);
            return rasterizer.Calls;
        }

        Assert.Equal(1, await CallsAfter(TimeSpan.Zero));
        Assert.Equal(1, await CallsAfter(TimeSpan.FromSeconds(59)));    // минута не прошла
        Assert.Equal(2, await CallsAfter(TimeSpan.FromSeconds(1)));     // первая пауза — минута
        Assert.Equal(2, await CallsAfter(TimeSpan.FromSeconds(119)));   // вторая — уже две
        Assert.Equal(3, await CallsAfter(TimeSpan.FromSeconds(1)));
        Assert.Equal(3, await CallsAfter(TimeSpan.FromSeconds(239)));   // третья — четыре
        Assert.Equal(4, await CallsAfter(TimeSpan.FromSeconds(1)));
        Assert.Equal(4, await CallsAfter(TimeSpan.FromSeconds(299)));   // четвёртая упёрлась в предел: пять, а не восемь
        Assert.Equal(5, await CallsAfter(TimeSpan.FromSeconds(1)));
    }

    /// <summary>Счёт отказов — у логотипа: новый логотип начинает с первой, короткой паузы.</summary>
    [Fact]
    public async Task Новый_логотип_начинает_паузу_заново()
    {
        var hands = new Hands();
        var rasterizer = new Counting { Answer = new RasterizedLogo(null, BrandIconProblem.Builder) };
        var store = new BrandIconStore(rasterizer, NullLogger<BrandIconStore>.Instance) { Clock = () => hands.Now };

        await store.EnsureAsync("logo:1", Bytes, default);
        hands.Now += 60_000;
        await store.EnsureAsync("logo:1", Bytes, default);              // второй отказ: пауза уже две минуты
        await store.EnsureAsync("logo:2", Bytes, default);              // другой логотип
        hands.Now += 60_000;
        await store.EnsureAsync("logo:2", Bytes, default);

        Assert.Equal(4, rasterizer.Calls);
    }

    /// <summary>
    /// Хранилище приняло соединение и молчит. Построения ждёт название в шапке каждого экрана —
    /// срок обязан истечь, а отказ запомниться: иначе каждый следующий в очереди ждал бы столько же.
    /// </summary>
    [Fact]
    public async Task Молчащее_хранилище_упирается_в_срок_и_запоминается_сбоем()
    {
        var reads = 0;
        var rasterizer = new Counting();
        var store = new BrandIconStore(rasterizer, NullLogger<BrandIconStore>.Instance)
        {
            BuildDeadline = TimeSpan.FromMilliseconds(100),
        };

        async Task<byte[]?> Silent(CancellationToken ct)
        {
            reads++;
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        }

        var first = await store.EnsureAsync("logo:1", Silent, default);
        var second = await store.EnsureAsync("logo:1", Silent, default);

        Assert.Equal((null, BrandIconProblem.Builder), (first.Set, first.Problem));
        Assert.Same(first, second);
        Assert.Equal(1, reads);
        Assert.Equal(0, rasterizer.Calls);
    }

    /// <summary>
    /// То же, но чтение отмену НЕ СЛЫШИТ — так читает тело файла клиент хранилища, замершего
    /// посреди ответа. Срок, полагающийся на сговорчивость чтения, здесь не истёк бы никогда, и за
    /// общим замком стояли бы все запросы оформления.
    /// </summary>
    [Fact]
    public async Task Чтение_глухое_к_отмене_срок_всё_равно_обрывает()
    {
        var reads = 0;
        var deaf = new TaskCompletionSource<byte[]?>();
        var store = new BrandIconStore(new Counting(), NullLogger<BrandIconStore>.Instance)
        {
            BuildDeadline = TimeSpan.FromMilliseconds(100),
        };

        Task<byte[]?> Deaf(CancellationToken _)
        {
            reads++;
            return deaf.Task;
        }

        var first = await store.EnsureAsync("logo:1", Deaf, default).WaitAsync(TimeSpan.FromSeconds(20));
        var second = await store.EnsureAsync("logo:1", Deaf, default).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal((null, BrandIconProblem.Builder), (first.Set, first.Problem));
        Assert.Same(first, second);
        Assert.Equal(1, reads);
    }

    /// <summary>
    /// Ушёл человек — это отмена, а не сбой: запомненная, она оставила бы без значков следующего,
    /// у которого всё построилось бы.
    /// </summary>
    [Fact]
    public async Task Отмена_вызывающего_не_запоминается()
    {
        var rasterizer = new Counting { Delay = TimeSpan.FromSeconds(30) };
        var store = Store(rasterizer);
        using var gone = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.EnsureAsync("logo:1", Bytes, gone.Token));

        // Ячейка пуста: тот же логотип тут же строится заново — и строится.
        rasterizer.Delay = TimeSpan.Zero;
        var next = await store.EnsureAsync("logo:1", Bytes, default);

        Assert.NotNull(next.Set);
        Assert.Equal(2, rasterizer.Calls);
    }

    /// <summary>Значок стоит на странице входа: исключение построителя не должно дойти до неё.</summary>
    [Fact]
    public async Task Исключение_построителя_становится_состоянием_а_не_отказом()
    {
        var store = Store(new Counting { Throw = true });

        var build = await store.EnsureAsync("logo:1", Bytes, default);

        Assert.Equal((null, BrandIconProblem.Builder), (build.Set, build.Problem));
    }

    [Fact]
    public async Task Логотип_не_читается_из_хранилища_сбой_сервера_а_не_файла()
    {
        var rasterizer = new Counting();

        var build = await Store(rasterizer).EnsureAsync("logo:1", _ => Task.FromResult<byte[]?>(null), default);

        Assert.Equal(BrandIconProblem.Builder, build.Problem);
        Assert.Equal(0, rasterizer.Calls);
    }

    /// <summary>
    /// Предел в байтах размеров не ограничивает: маленький файл разворачивается в гигабайты. До
    /// растеризатора такая картинка не доходит.
    /// </summary>
    [Fact]
    public async Task Картинка_слишком_большая_в_точках_не_растрируется()
    {
        var rasterizer = new Counting();

        var build = await Store(rasterizer).EnsureAsync(
            "logo:1", _ => Task.FromResult<byte[]?>(PngHeader(20000, 20000)), default);

        Assert.Equal(BrandIconProblem.Size, build.Problem);
        Assert.Equal(0, rasterizer.Calls);
    }

    /// <summary>
    /// PNG из заголовка и начала данных: размеры читаются из заголовка, до точек дело не доходит.
    /// Настоящая картинка 20000×20000 заняла бы в тесте полтора гигабайта — то самое, от чего
    /// предел и защищает.
    /// </summary>
    private static byte[] PngHeader(int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;   // глубина
        header[9] = 6;   // RGBA
        Chunk(ms, "IHDR"u8, header);
        // Читатель считает заголовок прочитанным, только дойдя до блока данных.
        Chunk(ms, "IDAT"u8, [0x78, 0x9C]);
        return ms.ToArray();
    }

    private static void Chunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);

        Span<byte> body = stackalloc byte[type.Length + data.Length];
        type.CopyTo(body);
        data.CopyTo(body[type.Length..]);
        stream.Write(body);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32.HashToUInt32(body));
        stream.Write(number);
    }

    /// <summary>Растеризатор-счётчик: отдаёт сам логотип как растр либо заданный отказ.</summary>
    private sealed class Counting : ILogoRasterizer
    {
        private int calls;
        public int Calls => calls;
        public RasterizedLogo? Answer { get; set; }
        public TimeSpan Delay { get; set; }
        public bool Throw { get; init; }

        public async Task<RasterizedLogo> RasterizeAsync(byte[] logo, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            if (Throw) throw new IOException("диск переполнен");
            return Answer ?? new RasterizedLogo(logo, null);
        }
    }
}
