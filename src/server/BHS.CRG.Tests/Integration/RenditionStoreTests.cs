using System.Net;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Api.Renditions;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static BHS.CRG.Tests.Renditions.RenditionFixtures;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Хранение читаемого образа (issue #1269): построен один раз, дальше отдаётся записанное.
///
/// <para>Конвертер здесь подставной и считает обращения: главное обещание службы — «второй вопрос
/// о том же файле конвертер не запускает» — иначе как счётчиком не проверить.</para>
/// </summary>
[Collection("Integration")]
public class RenditionStoreTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    // Оригиналы тесты кладут от имени ядра; служба читает от имени того, кто спрашивает (ТЗ CORE-37.2).
    private const string Owner = BlobOwner.Core;

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        // Записи образов несут отпечаток системного профиля печати, а таблица профилей между классами
        // не усекается: правленый соседом системный профиль дал бы здесь чужой отпечаток.
        await PrintProfilesState.ResetAsync(fixture.Services);
    }
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly string[] Words = ["invoice", "supplier", "total"];

    private IBlobStorage Blobs => fixture.Services.GetRequiredService<IBlobStorage>();
    private FakeBlobStorage Storage => fixture.Services.GetRequiredService<FakeBlobStorage>();

    private RenditionStore Store(StubConverter converter) => new(
        fixture.Services.GetRequiredService<IServiceScopeFactory>(), Blobs,
        Service(converter.HandleAsync, converter.Version), NullLogger<RenditionStore>.Instance);

    private Task<string> UploadBookAsync() =>
        Blobs.UploadAsync("счёт.xlsx", new MemoryStream(Workbook(Words)), "application/octet-stream");

    private async Task<List<RenditionRecord>> RecordsAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Renditions.AsNoTracking().ToListAsync();
    }

    private async Task<bool> InRegistryAsync(string path)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().BlobRegistry.AnyAsync(e => e.Path == path);
    }

    [Fact]
    public async Task Офисный_файл_получает_образ_и_запись_о_нём()
    {
        var converter = StubConverter.Converts(Words);
        var original = await UploadBookAsync();

        var record = await Store(converter).EnsureAsync(original, Owner, CancellationToken.None);

        Assert.Equal(RenditionState.Built, record.State);
        Assert.Equal(1, record.Pages);
        Assert.Equal("gotenberg 8.37.0", record.Converter);
        Assert.True(Storage.Exists(record.ImageBlobPath!));
        // Образ — такой же файл хранилища, с записью в реестре: без неё его не отдали бы на чтение.
        Assert.True(await InRegistryAsync(record.ImageBlobPath!));
        Assert.Equal(original, Assert.Single(await RecordsAsync()).OriginalBlobPath);
    }

    /// <summary>
    /// Файл модуля (ТЗ CORE-37.2): оригинал читается от имени модуля, образ ложится ЕГО файлом — модуль
    /// открывает образ своим портом, — а спросивший от чужого имени не видит ни записи, ни оригинала.
    /// </summary>
    [Fact]
    public async Task Образ_файла_модуля_принадлежит_модулю_и_чужому_не_виден()
    {
        const string costs = "costs";
        var original = await Blobs.UploadAsync(
            "счёт.xlsx", new MemoryStream(Workbook(Words)), "application/octet-stream", costs);
        var store = Store(StubConverter.Converts(Words));

        var record = await store.EnsureAsync(original, costs, CancellationToken.None);

        Assert.Equal(RenditionState.Built, record.State);
        Assert.Equal(costs, await OwnerAsync(record.ImageBlobPath!));
        await using (var image = await new ModuleBlobsPort(Blobs, costs).OpenAsync(record.ImageBlobPath!))
            Assert.True(image.CanRead);

        Assert.Null(await store.FindAsync(original, "work", CancellationToken.None));
        Assert.Null(await store.FindShownAsync(original, BlobOwner.Core, CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(
            () => store.EnsureAsync(original, BlobOwner.Core, CancellationToken.None));
    }

    private async Task<string> OwnerAsync(string path)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().BlobRegistry
            .Where(e => e.Path == path).Select(e => e.Owner).SingleAsync();
    }

    /// <summary>
    /// Книга без своей раскладки: в записи — след раскладки и снимок профиля (issue #1453), а оригинал
    /// в хранилище побайтно тот же — вписывается КОПИЯ.
    /// </summary>
    [Fact]
    public async Task Вписанная_книга_оставляет_след_раскладки_а_оригинал_цел()
    {
        var book = Workbook(b => b.CreateSheet("Sheet").CreateRow(0).CreateCell(0).SetCellValue(string.Join(" ", Words)),
            ownLayout: false);
        var original = await Blobs.UploadAsync("счёт.xlsx", new MemoryStream(book), "application/octet-stream");

        await Store(StubConverter.Converts(Words)).EnsureAsync(original, Owner, CancellationToken.None);

        var record = Assert.Single(await RecordsAsync());
        Assert.Equal(RenditionLayout.Applied, record.Layout);
        Assert.Contains("\"profile\": \"по умолчанию\"", record.LayoutApplied);
        // Первое построение — по системному профилю, и запись помнит его с отпечатком (issue #1454).
        Assert.Equal((RenditionLayoutProfile.SystemId, RenditionLayoutProfile.System().Stamp),
            (record.LayoutProfileId, record.LayoutStamp));
        await using var stored = await Blobs.DownloadAsync(original);
        using var copy = new MemoryStream();
        await stored.CopyToAsync(copy);
        Assert.Equal(book, copy.ToArray());
    }

    [Fact]
    public async Task Повторный_вопрос_о_том_же_файле_конвертер_не_запускает()
    {
        var converter = StubConverter.Converts(Words);
        var store = Store(converter);
        var original = await UploadBookAsync();

        var first = await store.EnsureAsync(original, Owner, CancellationToken.None);
        var second = await store.EnsureAsync(original, Owner, CancellationToken.None);

        Assert.Equal(1, converter.Conversions);
        Assert.Equal(first.ImageBlobPath, second.ImageBlobPath);
    }

    /// <summary>
    /// Запись отдаётся как есть, каким бы конвертером она ни сделана: обновился сервис — образ
    /// прежний, пока его не перестроили руками.
    /// </summary>
    [Fact]
    public async Task Обновление_конвертера_образ_молча_не_перестраивает()
    {
        var original = await UploadBookAsync();
        var before = await Store(StubConverter.Converts(Words)).EnsureAsync(original, Owner, CancellationToken.None);

        var newer = StubConverter.Converts(Words, version: "9.0.0");
        var after = await Store(newer).EnsureAsync(original, Owner, CancellationToken.None);

        Assert.Equal(0, newer.Conversions);
        Assert.Equal(before.ImageBlobPath, after.ImageBlobPath);
        Assert.Equal("gotenberg 8.37.0", after.Converter);
    }

    [Fact]
    public async Task Два_одновременных_вопроса_строят_образ_один_раз()
    {
        var converter = StubConverter.Converts(Words, delay: TimeSpan.FromMilliseconds(300));
        var store = Store(converter);
        var original = await UploadBookAsync();

        var both = await Task.WhenAll(
            store.EnsureAsync(original, Owner, CancellationToken.None),
            store.EnsureAsync(original, Owner, CancellationToken.None));

        Assert.Equal(1, converter.Conversions);
        Assert.Equal(both[0].ImageBlobPath, both[1].ImageBlobPath);
        Assert.Single(await RecordsAsync());
    }

    [Fact]
    public async Task Файл_который_читается_сам_образа_не_получает()
    {
        var converter = new StubConverter(MustNotBeCalled);
        var original = await Blobs.UploadAsync("скан.pdf", new MemoryStream(Pdf("anything")), "application/pdf");

        var record = await Store(converter).EnsureAsync(original, Owner, CancellationToken.None);

        Assert.Equal(RenditionState.AsIs, record.State);
        Assert.Equal("application/pdf", record.Mime);
        Assert.Null(record.ImageBlobPath);
    }

    /// <summary>Отказ о файле не изменится, сколько ни спрашивай, — и строить заново незачем.</summary>
    [Fact]
    public async Task Отказ_о_файле_запоминается()
    {
        var original = await UploadBookAsync();
        var first = await Store(new StubConverter(Answers(HttpStatusCode.BadRequest))).EnsureAsync(original, Owner, CancellationToken.None);

        var second = await Store(new StubConverter(MustNotBeCalled)).EnsureAsync(original, Owner, CancellationToken.None);

        Assert.Equal(RenditionState.Refused, first.State);
        Assert.Equal("Failed", second.RefusalKind);
        Assert.Equal(first.RefusalReason, second.RefusalReason);
    }

    /// <summary>
    /// Отказ о сервисе — не свойство файла. Запомни мы его, файл, приложенный в минуту перезапуска
    /// конвертера, остался бы без образа, пока кто-нибудь не нажмёт «Перестроить».
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "http://converter:3000", "Unavailable")]
    [InlineData(HttpStatusCode.NotFound, "http://converter:3000", "NotSetUp")]
    [InlineData(HttpStatusCode.OK, null, "NotSetUp")]
    public async Task Отказ_сервиса_не_запоминается(HttpStatusCode status, string? baseUrl, string kind)
    {
        var original = await UploadBookAsync();
        var down = new RenditionStore(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(), Blobs,
            new RenditionService(new(Client(Answers(status), baseUrl), NullLogger<BHS.CRG.Infrastructure.Renditions.OfficeRenditionBuilder>.Instance)),
            NullLogger<RenditionStore>.Instance);

        var refused = await down.EnsureAsync(original, Owner, CancellationToken.None);

        Assert.Equal(kind, refused.RefusalKind);
        Assert.Empty(await RecordsAsync());

        var built = await Store(StubConverter.Converts(Words)).EnsureAsync(original, Owner, CancellationToken.None);
        Assert.Equal(RenditionState.Built, built.State);
    }

    /// <summary>
    /// «Файл другого вида» — ответ версии приложения, а не свойство файла: какие виды читаются,
    /// решает реестр видов, и он растёт. Запомненный, он пережил бы обновление.
    /// </summary>
    [Fact]
    public async Task Отказ_файл_другого_вида_не_запоминается()
    {
        var original = await Blobs.UploadAsync("заметка.txt", new MemoryStream("просто текст"u8.ToArray()), "text/plain");

        var refused = await Store(new StubConverter(MustNotBeCalled)).EnsureAsync(original, Owner, CancellationToken.None);

        Assert.Equal("WrongFormat", refused.RefusalKind);
        Assert.Empty(await RecordsAsync());
    }

    /// <summary>
    /// Построение длится секунду, а файл заменяют сразу после загрузки. Запись, сделанная после
    /// удаления оригинала, держала бы образ файла, которого нет, — и уборка её не убрала бы никогда.
    /// </summary>
    [Fact]
    public async Task Оригинал_удалён_пока_строился_образ_не_остаётся_ни_записи_ни_образа()
    {
        var original = await UploadBookAsync();
        var converter = new StubConverter(async (_, _) =>
        {
            await Blobs.DeleteAsync(original);
            return PdfReply(Pdf(string.Join(" ", Words)));
        });

        await Assert.ThrowsAsync<NotFoundException>(() => Store(converter).EnsureAsync(original, Owner, CancellationToken.None));

        Assert.Empty(await RecordsAsync());
        using var scope = fixture.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().BlobRegistry.CountAsync());
    }

    /// <summary>Скану хватает начала: тянуть из хранилища десятки мегабайт ради «читается сам» незачем.</summary>
    [Fact]
    public async Task Файл_который_читается_сам_целиком_из_хранилища_не_читают()
    {
        var scan = new byte[2 * 1024 * 1024];
        Pdf("anything").CopyTo(scan, 0);
        var original = await Blobs.UploadAsync("скан.pdf", new MemoryStream(scan), "application/pdf");
        var tapped = new TappedBlobs(Blobs);
        var store = new RenditionStore(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(), tapped,
            Service(MustNotBeCalled), NullLogger<RenditionStore>.Instance);

        var record = await store.EnsureAsync(original, Owner, CancellationToken.None);

        Assert.Equal(RenditionState.AsIs, record.State);
        Assert.InRange(tapped.BytesRead, 1, 64 * 1024);
    }

    /// <summary>
    /// Оригинала уже нет — и это успех вызова: владелец по нему снимает ссылку у своей записи.
    /// Отказ хранилища на образе наверх не уходит; образ остаётся без держателя, для уборки.
    /// </summary>
    [Fact]
    public async Task Не_удалившийся_образ_удаление_оригинала_не_роняет()
    {
        var original = await UploadBookAsync();
        var record = await Store(StubConverter.Converts(Words)).EnsureAsync(original, Owner, CancellationToken.None);
        var stubborn = new BHS.CRG.Infrastructure.Storage.RegisteredBlobStorage(
            new TappedBlobs(Storage) { FailDeleteOf = record.ImageBlobPath },
            fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BHS.CRG.Infrastructure.Storage.RegisteredBlobStorage>.Instance);

        await stubborn.DeleteAsync(original);

        Assert.False(Storage.Exists(original));
        Assert.Empty(await RecordsAsync());
        Assert.True(Storage.Exists(record.ImageBlobPath!));
    }

    [Fact]
    public async Task Чтение_записи_ничего_не_строит()
    {
        var converter = new StubConverter(MustNotBeCalled);
        var original = await UploadBookAsync();

        Assert.Null(await Store(converter).FindAsync(original, Owner, CancellationToken.None));
        Assert.Empty(await RecordsAsync());
    }

    [Fact]
    public async Task Оригинала_нет_отказ_а_не_запись()
    {
        var store = Store(new StubConverter(MustNotBeCalled));

        await Assert.ThrowsAsync<NotFoundException>(() => store.EnsureAsync("bucket/2026/10/10/нет.xlsx", Owner, CancellationToken.None));
        Assert.Empty(await RecordsAsync());
    }

    /// <summary>
    /// Привязка образа к оригиналу — в хранилище ядра: владелец файла зовёт удаление как раньше и об
    /// образе не знает.
    /// </summary>
    [Fact]
    public async Task Удаление_оригинала_убирает_образ_и_запись()
    {
        var original = await UploadBookAsync();
        var record = await Store(StubConverter.Converts(Words)).EnsureAsync(original, Owner, CancellationToken.None);

        await Blobs.DeleteAsync(original);

        Assert.False(Storage.Exists(record.ImageBlobPath!));
        Assert.False(await InRegistryAsync(record.ImageBlobPath!));
        Assert.Empty(await RecordsAsync());
    }

    [Fact]
    public async Task Перестроить_кладёт_новый_образ_под_новым_путём()
    {
        var converter = StubConverter.Converts(Words);
        var store = Store(converter);
        var original = await UploadBookAsync();
        var before = await store.EnsureAsync(original, Owner, CancellationToken.None);

        var after = await Store(StubConverter.Converts(Words, version: "9.0.0")).RebuildAsync(original, Owner, null, CancellationToken.None);

        Assert.NotEqual(before.ImageBlobPath, after.ImageBlobPath);
        Assert.True(Storage.Exists(after.ImageBlobPath!));
        Assert.Equal("gotenberg 9.0.0", after.Converter);
        // Запись у пути по-прежнему одна, и отвечает она новым образом.
        Assert.Equal(after.ImageBlobPath, Assert.Single(await RecordsAsync()).ImageBlobPath);
        Assert.Equal(after.ImageBlobPath, (await store.EnsureAsync(original, Owner, CancellationToken.None)).ImageBlobPath);
    }

    /// <summary>
    /// Файл тот же, что и тогда, когда образ вышел: отказ сейчас говорит о сегодняшнем конвертере —
    /// занят, упал, не успел. Затри он запись — образ, по которому распознаны значения, остался бы
    /// без держателя и ушёл бы следующей уборкой.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Отказ_при_перестроении_построенный_образ_не_затирает(HttpStatusCode status)
    {
        var original = await UploadBookAsync();
        var before = await Store(StubConverter.Converts(Words)).EnsureAsync(original, Owner, CancellationToken.None);

        var refused = await Store(new StubConverter(Answers(status))).RebuildAsync(original, Owner, null, CancellationToken.None);

        Assert.Equal(RenditionState.Refused, refused.State);
        var kept = Assert.Single(await RecordsAsync());
        Assert.Equal(before.ImageBlobPath, kept.ImageBlobPath);
        Assert.True(Storage.Exists(before.ImageBlobPath!));
    }

    /// <summary>
    /// Построение пишет в свои две таблицы и больше никуда. Версия счёта — версия его строки:
    /// запись образа в строку владельца дала бы «счёт тем временем изменили» на открытой форме.
    /// </summary>
    [Fact]
    public async Task Построение_пишет_только_в_реестр_файлов_и_в_таблицу_образов()
    {
        var original = await UploadBookAsync();
        await Store(StubConverter.Converts(Words)).EnsureAsync(original, Owner, CancellationToken.None);

        using var scope = fixture.Services.CreateScope();
        var touched = await IntegrationTestFixture.TouchedTablesAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());

        Assert.Equal(["blob_registry", "renditions"], touched.Order());
    }

    // ── Порт для модулей ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Модуль_получает_путь_образа_а_у_читаемого_файла_путь_самого_файла()
    {
        var port = new ModuleRenditionsPort(Store(StubConverter.Converts(Words)), Owner, NullLogger<ModuleRenditionsPort>.Instance);
        var book = await UploadBookAsync();
        var scan = await Blobs.UploadAsync("скан.pdf", new MemoryStream(Pdf("anything")), "application/pdf");

        var built = Assert.IsType<ModuleRendition.Built>(await port.EnsureAsync(book));
        var asIs = Assert.IsType<ModuleRendition.AsIs>(await port.EnsureAsync(scan));

        Assert.NotEqual(book, built.Path);
        Assert.True(Storage.Exists(built.Path));
        Assert.Equal("gotenberg 8.37.0", built.Converter);
        Assert.Equal(scan, asIs.Path);
        Assert.Equal(built, await port.FindAsync(book), ModuleRenditionComparer.Instance);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Модуль_узнаёт_поможет_ли_повтор(HttpStatusCode status, bool retryHelps)
    {
        var port = new ModuleRenditionsPort(Store(new StubConverter(Answers(status))), Owner, NullLogger<ModuleRenditionsPort>.Instance);

        var refused = Assert.IsType<ModuleRendition.Refused>(await port.EnsureAsync(await UploadBookAsync()));

        Assert.Equal(retryHelps, refused.RetryHelps);
        Assert.NotEmpty(refused.Reason);
    }

    /// <summary>Запись с видом отказа, которого эта версия не знает, читается: повтор не обещан.</summary>
    [Fact]
    public async Task Неизвестный_вид_отказа_в_записи_порт_не_роняет()
    {
        var original = await UploadBookAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Renditions.Add(RenditionRecord.Refused(original, "ВидИзДругойВерсии", "Причина словами."));
            await db.SaveChangesAsync();
        }
        var port = new ModuleRenditionsPort(Store(new StubConverter(MustNotBeCalled)), Owner, NullLogger<ModuleRenditionsPort>.Instance);

        var refused = Assert.IsType<ModuleRendition.Refused>(await port.EnsureAsync(original));

        Assert.False(refused.RetryHelps);
        Assert.Equal("Причина словами.", refused.Reason);
    }

    /// <summary>Пометки — список, а у записей сравнение списков ссылочное; сверяем по содержимому.</summary>
    private sealed class ModuleRenditionComparer : IEqualityComparer<ModuleRendition?>
    {
        public static readonly ModuleRenditionComparer Instance = new();

        public bool Equals(ModuleRendition? x, ModuleRendition? y) =>
            x is ModuleRendition.Built a && y is ModuleRendition.Built b
            && a.Path == b.Path && a.Pages == b.Pages && a.Converter == b.Converter && a.Notes.SequenceEqual(b.Notes);

        public int GetHashCode(ModuleRendition? value) => 0;
    }
}

/// <summary>Хранилище с подслушиванием: сколько байт из него прочитали и на каком пути удаление отказывает.</summary>
internal sealed class TappedBlobs(IBlobStorage inner) : IBlobStorage
{
    private long _bytesRead;

    public long BytesRead => _bytesRead;

    public string? FailDeleteOf { get; init; }

    public Task<string> UploadAsync(string fileName, Stream content, string contentType, CancellationToken ct = default) =>
        inner.UploadAsync(fileName, content, contentType, ct);

    public Task PutAsync(string blobPath, Stream content, string contentType, CancellationToken ct = default) =>
        inner.PutAsync(blobPath, content, contentType, ct);

    public Task<long?> GetSizeAsync(string blobPath, CancellationToken ct = default) => inner.GetSizeAsync(blobPath, ct);

    public Task DeleteAsync(string blobPath, CancellationToken ct = default) =>
        blobPath == FailDeleteOf ? throw new IOException("Хранилище отказало") : inner.DeleteAsync(blobPath, ct);

    public async Task<Stream> DownloadAsync(string blobPath, CancellationToken ct = default) =>
        new Tap(await inner.DownloadAsync(blobPath, ct), this);

    // Владельца обёртка передаёт дальше, а не теряет: служба образов читает от имени модуля (ТЗ CORE-37.2),
    // и подслушивание не должно превращать его в ядро.
    public Task<string> UploadAsync(string fileName, Stream content, string contentType, string owner, CancellationToken ct = default) =>
        inner.UploadAsync(fileName, content, contentType, owner, ct);

    public Task PutAsync(string blobPath, Stream content, string contentType, string owner, CancellationToken ct = default) =>
        inner.PutAsync(blobPath, content, contentType, owner, ct);

    public Task<long?> GetSizeAsync(string blobPath, string owner, CancellationToken ct = default) =>
        inner.GetSizeAsync(blobPath, owner, ct);

    public Task<IReadOnlySet<string>> FindAbsentAsync(IEnumerable<string> blobPaths, string owner, CancellationToken ct = default) =>
        inner.FindAbsentAsync(blobPaths, owner, ct);

    public Task DeleteAsync(string blobPath, string owner, CancellationToken ct = default) =>
        blobPath == FailDeleteOf ? throw new IOException("Хранилище отказало") : inner.DeleteAsync(blobPath, owner, ct);

    public async Task<Stream> DownloadAsync(string blobPath, string owner, CancellationToken ct = default) =>
        new Tap(await inner.DownloadAsync(blobPath, owner, ct), this);

    private sealed class Tap(Stream source, TappedBlobs owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            Interlocked.Add(ref owner._bytesRead, read);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>Подставной конвертер, который считает преобразования и называет свою версию.</summary>
internal sealed class StubConverter(Converter convert, string version = "8.37.0")
{
    private int _conversions;

    public int Conversions => _conversions;

    public string Version => version;

    public static StubConverter Converts(string[] words, string version = "8.37.0", TimeSpan? delay = null) =>
        new(async (_, ct) =>
        {
            if (delay is { } wait) await Task.Delay(wait, ct);
            return PdfReply(Pdf(string.Join(" ", words)));
        }, version);

    public Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _conversions);
        return convert(request, ct);
    }
}
