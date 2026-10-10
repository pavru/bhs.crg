using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Api.Renditions;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.QualityDocs;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Maintenance;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static BHS.CRG.Tests.Renditions.RenditionFixtures;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Читаемый образ под уборкой осиротевших файлов и в резервной копии (issue #1269) — два места,
/// которые таблица образов могла сломать молча.
///
/// <para><b>Уборка.</b> В записи образа два пути. Путь образа — держатель: пока запись есть, образ
/// живой. Путь оригинала держателем быть не должен: сочти его уборка ссылкой — и ни один оригинал,
/// у которого есть образ, не стал бы осиротевшим никогда, а хранилище росло бы без единого отказа.</para>
///
/// <para><b>Копия.</b> Образ — функция от оригинала, и в копию он не едет: после восстановления его
/// строит заново тот конвертер, который стоит на новом месте.</para>
/// </summary>
[Collection("Integration")]
public class RenditionCleanupTests(IntegrationTestFixture fixture) : IAsyncLifetime
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

    private async Task<OrphanBlobReport> CleanAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<OrphanBlobCleanup>().RunAsync(dryRun: false, minAgeHours: 0);
    }

    private async Task<int> RecordsAsync()
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Renditions.CountAsync();
    }

    /// <summary>Владелец оригинала — документ качества ядра: его скан едет и в уборку, и в копию.</summary>
    private async Task HoldAsync(string scanPath)
    {
        using var scope = fixture.Services.CreateScope();
        var m = scope.ServiceProvider.GetRequiredService<IMediator>();
        var type = await m.Send(new CreateDocumentTypeCommand(
            "Сертификат", "CERT", DocumentTypeKind.Document, null, JsonDocument.Parse("""{"fields":[]}""")));
        await m.Send(new CreateQualityDocumentCommand(
            type.Id, "Сертификат 1", JsonDocument.Parse("{}"), CatalogScope.System, null, QualityDocSource.Manual,
            scanPath, "счёт.xlsx", "application/octet-stream"));
    }

    [Fact]
    public async Task Оригинал_от_которого_отказался_владелец_осиротевает_вместе_с_образом()
    {
        var original = await UploadBookAsync();
        var record = await Store(StubConverter.Converts(Words)).EnsureAsync(original, CancellationToken.None);

        var report = await CleanAsync();

        // Осиротел один — оригинал: образ держит его запись. Уходят оба: удаление оригинала
        // уносит образ с собой.
        Assert.Equal(1, report.Orphans);
        Assert.Equal(1, report.Deleted);
        Assert.False(Storage.Exists(original));
        Assert.False(Storage.Exists(record.ImageBlobPath!));
        Assert.Equal(0, await RecordsAsync());
    }

    [Fact]
    public async Task Образ_живого_оригинала_уборка_не_трогает()
    {
        var original = await UploadBookAsync();
        await HoldAsync(original);
        var record = await Store(StubConverter.Converts(Words)).EnsureAsync(original, CancellationToken.None);

        var report = await CleanAsync();

        Assert.Equal(0, report.Orphans);
        Assert.True(Storage.Exists(original));
        Assert.True(Storage.Exists(record.ImageBlobPath!));
    }

    /// <summary>
    /// Перестроение прежний образ не удаляет — помнит ли о нём кто-то, знает только уборка. Здесь
    /// не помнит никто, и уборка его забирает; новый остаётся.
    /// </summary>
    [Fact]
    public async Task Прежний_образ_после_перестроения_забирает_уборка()
    {
        var original = await UploadBookAsync();
        await HoldAsync(original);
        var store = Store(StubConverter.Converts(Words));
        var before = await store.EnsureAsync(original, CancellationToken.None);
        var after = await store.RebuildAsync(original, CancellationToken.None);
        Assert.True(Storage.Exists(before.ImageBlobPath!));

        var report = await CleanAsync();

        Assert.Equal(1, report.Deleted);
        Assert.False(Storage.Exists(before.ImageBlobPath!));
        Assert.True(Storage.Exists(after.ImageBlobPath!));
        Assert.True(Storage.Exists(original));
    }

    [Fact]
    public async Task В_копию_образ_не_едет_а_после_восстановления_строится_заново()
    {
        var original = await UploadBookAsync();
        await HoldAsync(original);
        var record = await Store(StubConverter.Converts(Words)).EnsureAsync(original, CancellationToken.None);

        var copy = new MemoryStream();
        using (var scope = fixture.Services.CreateScope())
        {
            var (zip, _) = await scope.ServiceProvider.GetRequiredService<BackupService>().ExportAsync(BackupScope.Full);
            await using (zip) await zip.CopyToAsync(copy);
        }

        using (var archive = new ZipArchive(new MemoryStream(copy.ToArray()), ZipArchiveMode.Read))
        {
            // Оригинал в копии есть — иначе отсутствие образа ничего бы не значило.
            Assert.Contains(archive.Entries, e => e.FullName == "blobs/" + original);
            Assert.DoesNotContain(archive.Entries, e => e.FullName == "blobs/" + record.ImageBlobPath);
            using var manifest = new StreamReader(archive.GetEntry("manifest.json")!.Open());
            Assert.DoesNotContain(RenditionStore.ImageFileName, await manifest.ReadToEndAsync());
        }

        // Новое место: ни файлов, ни записи об образе.
        await Blobs.DeleteAsync(original);
        Assert.Equal(0, await RecordsAsync());

        using (var scope = fixture.Services.CreateScope())
        {
            copy.Position = 0;
            var report = await scope.ServiceProvider.GetRequiredService<BackupService>().ImportAsync(copy);
            Assert.True(report.Success, string.Join("; ", report.Warnings));
        }

        Assert.True(Storage.Exists(original));
        Assert.Equal(0, await RecordsAsync());

        var converter = StubConverter.Converts(Words, version: "9.0.0");
        var rebuilt = await Store(converter).EnsureAsync(original, CancellationToken.None);

        Assert.Equal(1, converter.Conversions);
        Assert.Equal(RenditionState.Built, rebuilt.State);
        Assert.Equal("gotenberg 9.0.0", rebuilt.Converter);
    }
}
