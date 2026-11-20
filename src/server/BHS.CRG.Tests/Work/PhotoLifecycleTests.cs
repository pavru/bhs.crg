using System.Net;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Modules.Work.Photos;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Work;

/// <summary>Снятие, доставка и частичные сбои: регрессии исправлений, принесённых в рабочей копии work-17.</summary>
[Collection(AssignmentHost.Collection)]
public class PhotoLifecycleTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly PhotoWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Поздний_файл_снятого_фото_не_возвращает_его(bool announced)
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.DraftAsync(client, report, world.Reports.SiteA, line, announced ? [photo] : []);
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(client, report, line, photo, PhotoWorld.Shot())).StatusCode);
        var seen = await ReportWorld.JsonAsync(await client.GetAsync($"{ReportEndpoints.Path}/{report}"));
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.DeleteAsync(client, report, line, photo, ReviewWorld.Version(seen))).StatusCode);
        var entries = (await world.EntriesAsync(report)).Count;

        Assert.Equal(HttpStatusCode.Gone, (await PhotoWorld.UploadAsync(client, report, line, photo, PhotoWorld.Shot())).StatusCode);

        Assert.Empty(await world.PhotosAsync());
        Assert.Empty(await world.BlobsAsync());
        Assert.Equal(entries, (await world.EntriesAsync(report)).Count);
    }

    [Fact]
    public async Task Доставка_объявленного_фото_видна_в_since_без_смены_версии()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var draft = await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);
        var first = await ReportWorld.JsonAsync(await client.GetAsync(ReportEndpoints.Path));
        var before = first.GetProperty("cursor").GetInt64();

        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(client, report, line, photo, PhotoWorld.Shot())).StatusCode);
        var feed = await ReportWorld.JsonAsync(await client.GetAsync($"{ReportEndpoints.Path}?since={before}&limit=1"));

        var item = Assert.Single(feed.GetProperty("items").EnumerateArray());
        Assert.Equal(report, item.GetProperty("id").GetGuid());
        Assert.Equal("stored", item.GetProperty("lines")[0].GetProperty("photos")[0].GetProperty("state").GetString());
        Assert.Equal(ReviewWorld.Version(draft), ReviewWorld.Version(item));
        var after = feed.GetProperty("cursor").GetInt64();
        Assert.True(after > before);
        var empty = await ReportWorld.JsonAsync(await client.GetAsync($"{ReportEndpoints.Path}?since={after}"));
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Сбой_на_превью_убирает_уже_положенный_оригинал()
    {
        var (employee, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);
        using var scope = host.Services.CreateScope();
        var failing = new FailingPreview(scope.ServiceProvider.GetRequiredKeyedService<IModuleBlobs>(WorkModule.ModuleCode));
        var receiver = ActivatorUtilities.CreateInstance<LinePhotos>(scope.ServiceProvider, failing);
        using var body = new MemoryStream(PhotoWorld.Shot());

        await Assert.ThrowsAsync<IOException>(() => receiver.ReceiveAsync(ReportViewer.Own(employee), report, line, photo, body, default));

        Assert.NotNull(failing.OriginalPath);
        Assert.False(host.Services.GetRequiredService<FakeBlobStorage>().Exists(failing.OriginalPath));
        Assert.Empty(await world.BlobsAsync());
        Assert.Equal(LinePhotoState.Pending, Assert.Single(await world.PhotosAsync()).State);
    }

    [Fact]
    public async Task Устаревшее_чтение_не_снимает_фото_после_доставки()
    {
        var (employee, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        var stale = (await scope.ServiceProvider.GetRequiredService<ReportReads>().FindAsync(ReportViewer.Own(employee), report, default))!;
        var pending = Assert.Single(Assert.Single(stale.Lines).Photos);
        Assert.Equal(LinePhotoState.Pending, pending.State);
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(client, report, line, photo, PhotoWorld.Shot())).StatusCode);

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            stale.Touch(await db.NextReportSeqAsync(employee, default), DateTimeOffset.UtcNow);
            db.Remove(pending);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
            await tx.RollbackAsync();
        }

        Assert.Equal(LinePhotoState.Stored, Assert.Single(await world.PhotosAsync()).State);
        Assert.Equal(2, (await world.BlobsAsync()).Count);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/work/photos/{photo}")).StatusCode);
    }

    [Fact]
    public async Task Одновременные_загрузки_одного_фото_оставляют_одну_пару_файлов()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);

        var replies = await Task.WhenAll(Enumerable.Range(0, 2)
            .Select(_ => PhotoWorld.UploadAsync(client, report, line, photo, PhotoWorld.Shot())));

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(await replies[0].Content.ReadAsStringAsync(), await replies[1].Content.ReadAsStringAsync());
        Assert.Single(await world.PhotosAsync());
        Assert.Equal(2, (await world.BlobsAsync()).Count);
    }

    [Fact]
    public async Task Одновременные_повторы_удаления_возвращают_успех()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo, change) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var draft = await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);
        var url = PhotoWorld.LineUrl(report, line, photo) + $"?changeId={change}";

        var replies = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Delete, url, ReviewWorld.Version(draft)))));

        Assert.All(replies, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Empty(await world.PhotosAsync());
    }

    private sealed class FailingPreview(IModuleBlobs inner) : IModuleBlobs
    {
        public string? OriginalPath { get; private set; }

        public async Task<string> PutAsync(string fileName, Stream content, string contentType, CancellationToken ct = default)
        {
            if (fileName.EndsWith("-preview.jpg", StringComparison.Ordinal)) throw new IOException("preview upload failed");
            return OriginalPath = await inner.PutAsync(fileName, content, contentType, ct);
        }

        public Task<Stream> OpenAsync(string path, CancellationToken ct = default) => inner.OpenAsync(path, ct);
        public Task DeleteAsync(string path, CancellationToken ct = default) => inner.DeleteAsync(path, ct);
        public Task<long?> SizeAsync(string path, CancellationToken ct = default) => inner.SizeAsync(path, ct);
    }
}
