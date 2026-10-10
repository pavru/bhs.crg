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
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
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

        var record = await Store(converter).EnsureAsync(original, CancellationToken.None);

        Assert.Equal(RenditionState.Built, record.State);
        Assert.Equal(1, record.Pages);
        Assert.Equal("gotenberg 8.37.0", record.Converter);
        Assert.True(Storage.Exists(record.ImageBlobPath!));
        // Образ — такой же файл хранилища, с записью в реестре: без неё его не отдали бы на чтение.
        Assert.True(await InRegistryAsync(record.ImageBlobPath!));
        Assert.Equal(original, Assert.Single(await RecordsAsync()).OriginalBlobPath);
    }

    [Fact]
    public async Task Повторный_вопрос_о_том_же_файле_конвертер_не_запускает()
    {
        var converter = StubConverter.Converts(Words);
        var store = Store(converter);
        var original = await UploadBookAsync();

        var first = await store.EnsureAsync(original, CancellationToken.None);
        var second = await store.EnsureAsync(original, CancellationToken.None);

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
        var before = await Store(StubConverter.Converts(Words)).EnsureAsync(original, CancellationToken.None);

        var newer = StubConverter.Converts(Words, version: "9.0.0");
        var after = await Store(newer).EnsureAsync(original, CancellationToken.None);

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
            store.EnsureAsync(original, CancellationToken.None),
            store.EnsureAsync(original, CancellationToken.None));

        Assert.Equal(1, converter.Conversions);
        Assert.Equal(both[0].ImageBlobPath, both[1].ImageBlobPath);
        Assert.Single(await RecordsAsync());
    }

    [Fact]
    public async Task Файл_который_читается_сам_образа_не_получает()
    {
        var converter = new StubConverter(MustNotBeCalled);
        var original = await Blobs.UploadAsync("скан.pdf", new MemoryStream(Pdf("anything")), "application/pdf");

        var record = await Store(converter).EnsureAsync(original, CancellationToken.None);

        Assert.Equal(RenditionState.AsIs, record.State);
        Assert.Equal("application/pdf", record.Mime);
        Assert.Null(record.ImageBlobPath);
    }

    /// <summary>Отказ о файле не изменится, сколько ни спрашивай, — и строить заново незачем.</summary>
    [Fact]
    public async Task Отказ_о_файле_запоминается()
    {
        var original = await UploadBookAsync();
        var first = await Store(new StubConverter(Answers(HttpStatusCode.BadRequest))).EnsureAsync(original, CancellationToken.None);

        var second = await Store(new StubConverter(MustNotBeCalled)).EnsureAsync(original, CancellationToken.None);

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

        var refused = await down.EnsureAsync(original, CancellationToken.None);

        Assert.Equal(kind, refused.RefusalKind);
        Assert.Empty(await RecordsAsync());

        var built = await Store(StubConverter.Converts(Words)).EnsureAsync(original, CancellationToken.None);
        Assert.Equal(RenditionState.Built, built.State);
    }

    [Fact]
    public async Task Чтение_записи_ничего_не_строит()
    {
        var converter = new StubConverter(MustNotBeCalled);
        var original = await UploadBookAsync();

        Assert.Null(await Store(converter).FindAsync(original, CancellationToken.None));
        Assert.Empty(await RecordsAsync());
    }

    [Fact]
    public async Task Оригинала_нет_отказ_а_не_запись()
    {
        var store = Store(new StubConverter(MustNotBeCalled));

        await Assert.ThrowsAsync<NotFoundException>(() => store.EnsureAsync("bucket/2026/10/10/нет.xlsx", CancellationToken.None));
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
        var record = await Store(StubConverter.Converts(Words)).EnsureAsync(original, CancellationToken.None);

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
        var before = await store.EnsureAsync(original, CancellationToken.None);

        var after = await Store(StubConverter.Converts(Words, version: "9.0.0")).RebuildAsync(original, CancellationToken.None);

        Assert.NotEqual(before.ImageBlobPath, after.ImageBlobPath);
        Assert.True(Storage.Exists(after.ImageBlobPath!));
        Assert.Equal("gotenberg 9.0.0", after.Converter);
        // Запись у пути по-прежнему одна, и отвечает она новым образом.
        Assert.Equal(after.ImageBlobPath, Assert.Single(await RecordsAsync()).ImageBlobPath);
        Assert.Equal(after.ImageBlobPath, (await store.EnsureAsync(original, CancellationToken.None)).ImageBlobPath);
    }

    /// <summary>«Конвертер занят» — не причина потерять образ, который был.</summary>
    [Fact]
    public async Task Отказ_сервиса_при_перестроении_прежний_образ_не_трогает()
    {
        var original = await UploadBookAsync();
        var before = await Store(StubConverter.Converts(Words)).EnsureAsync(original, CancellationToken.None);

        var refused = await Store(new StubConverter(Answers(HttpStatusCode.BadGateway))).RebuildAsync(original, CancellationToken.None);

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
        await Store(StubConverter.Converts(Words)).EnsureAsync(original, CancellationToken.None);

        using var scope = fixture.Services.CreateScope();
        var touched = await IntegrationTestFixture.TouchedTablesAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());

        Assert.Equal(["blob_registry", "renditions"], touched.Order());
    }

    // ── Порт для модулей ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Модуль_получает_путь_образа_а_у_читаемого_файла_путь_самого_файла()
    {
        var port = new ModuleRenditionsPort(Store(StubConverter.Converts(Words)));
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
        var port = new ModuleRenditionsPort(Store(new StubConverter(Answers(status))));

        var refused = Assert.IsType<ModuleRendition.Refused>(await port.EnsureAsync(await UploadBookAsync()));

        Assert.Equal(retryHelps, refused.RetryHelps);
        Assert.NotEmpty(refused.Reason);
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
