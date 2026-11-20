using System.IO.Compression;
using System.Net;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Work;

/// <summary>
/// Круг «снял — восстановил» с фото строки (change <c>work-17-photos</c>, ТЗ WORK-35, CORE-26): снимок и превью едут в
/// копию файлами, возвращаются из неё и открываются по прежнему идентификатору; вес копии называет их долей модуля.
/// Образец — <c>InvoiceBackupRoundTripTests</c>.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class PhotoBackupRoundTripTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly PhotoWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Потеря двойная, как в жизни: нет ни фото в схеме модуля, ни файлов в хранилище. После восстановления фото и превью
    /// открываются по прежнему идентификатору, а копия не называет необъявленных колонок с файлами.
    /// </summary>
    [Fact]
    public async Task Фото_и_превью_возвращаются_из_копии()
    {
        var (_, installer) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.SubmittedAsync(installer, report, world.Reports.SiteA, line, photo);
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(installer, report, line, photo, PhotoWorld.Shot())).StatusCode);
        var original = await installer.GetByteArrayAsync($"/api/work/photos/{photo}");
        var preview = await installer.GetByteArrayAsync($"/api/work/photos/{photo}/preview");
        var paths = await world.BlobsAsync();
        Assert.Equal(2, paths.Count);

        var (archive, manifest) = await ExportAsync();

        var section = Assert.Single(manifest.ModuleData!, m => m.Module == WorkModule.ModuleCode);
        var row = Assert.Single(section.Tables.Single(t => t.Table == "line_photos").Rows);
        Assert.Equal(photo, row.GetProperty("id").GetGuid());
        Assert.All(paths, p => Assert.Contains(p, section.Files!));
        BackupSummary summary;
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.All(paths, p => Assert.NotNull(zip.GetEntry($"blobs/{p}")));
            using var passport = zip.GetEntry("summary.json")!.Open();
            summary = JsonSerializer.Deserialize<BackupSummary>(passport, JsonSerializerOptions.Web)!;
        }
        archive.Position = 0;
        // Модуль назвал обе колонки, где копия нашла путь: необъявленных нет.
        Assert.DoesNotContain(summary.Warnings ?? [], w => w.Contains("не сообщил") || w.Contains("внутри текста"));

        await LoseAsync(paths);
        Assert.Equal(HttpStatusCode.NotFound, (await installer.GetAsync($"/api/work/photos/{photo}")).StatusCode);

        RestoreReport restored;
        using (var scope = host.Services.CreateScope())
            restored = await scope.ServiceProvider.GetRequiredService<BackupService>().ImportAsync(archive);

        Assert.True(restored.Success, string.Join("; ", restored.Warnings));
        Assert.DoesNotContain(restored.Warnings, w => w.Contains("в хранилище они не найдены"));
        Assert.Equal(original, await installer.GetByteArrayAsync($"/api/work/photos/{photo}"));
        Assert.Equal(preview, await installer.GetByteArrayAsync($"/api/work/photos/{photo}/preview"));
    }

    /// <summary>Вес копии называет фото долей модуля «Учёт работ» — снимок и превью (WORK-35).</summary>
    [Fact]
    public async Task Вес_копии_считает_фото_модуля()
    {
        var (_, installer) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.SubmittedAsync(installer, report, world.Reports.SiteA, line, photo);
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(installer, report, line, photo, PhotoWorld.Shot())).StatusCode);
        var stored = Assert.Single(await world.PhotosAsync());

        using var scope = host.Services.CreateScope();
        var estimate = await scope.ServiceProvider.GetRequiredService<BackupService>()
            .EstimateSizeAsync(long.MaxValue, BackupScope.Full);

        var work = Assert.Single(estimate.Variant.Modules, m => m.Code == WorkModule.ModuleCode);
        Assert.Equal(("Учёт работ", 2), (work.Title, work.Count));
        Assert.True(work.Bytes > stored.Size, $"в вес модуля не вошло превью: {work.Bytes} байт при снимке {stored.Size}");
    }

    /// <summary>Потеря: строк фото и их отчётов нет, файлов в хранилище — тоже.</summary>
    private async Task LoseAsync(IEnumerable<string> paths)
    {
        await using (var conn = new NpgsqlConnection(host.Services.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"TRUNCATE {WorkDbContext.SchemaName}.\"line_photos\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        using var scope = host.Services.CreateScope();
        // От имени модуля: удаление от имени ядра файл work не тронуло бы (ТЗ CORE-37.2).
        foreach (var path in paths)
            await scope.ServiceProvider.GetRequiredService<IBlobStorage>().DeleteAsync(path, WorkModule.ModuleCode);
    }

    private async Task<(MemoryStream Archive, BackupManifest Manifest)> ExportAsync()
    {
        using var scope = host.Services.CreateScope();
        var (zip, _) = await scope.ServiceProvider.GetRequiredService<BackupService>().ExportAsync(BackupScope.Full);
        await using var handle = zip;
        var archive = new MemoryStream();
        await zip.CopyToAsync(archive);
        archive.Position = 0;

        BackupManifest manifest;
        using (var reader = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
        using (var entry = reader.GetEntry("manifest.json")!.Open())
            manifest = JsonSerializer.Deserialize<BackupManifest>(entry)!;
        archive.Position = 0;
        return (archive, manifest);
    }
}
