using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Tests.Generation;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Work;

/// <summary>Регрессии ревью work-17: запрос целиком, повтор удаления и выдача сохранённого файла.</summary>
[Collection(AssignmentHost.Collection)]
public class PhotoReviewTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly PhotoWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Фото_в_двух_строках_отказ_до_записи(bool editing)
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var draft = editing ? await world.DraftAsync(client, report, world.Reports.SiteA, line, photo) : (JsonElement?)null;
        var request = new
        {
            id = report, constructionId = world.Reports.SiteA, date = ReportWorld.Day16, hours = 7m,
            changeId = Guid.NewGuid(), lines = new[] { world.Line(line, photo), world.Line(Guid.NewGuid(), photo) },
        };

        var response = editing
            ? await ReportWorld.PutAsync(client, $"{ReportEndpoints.Path}/{report}", ReviewWorld.Version(draft!.Value), request)
            : await client.PostAsJsonAsync(ReportEndpoints.Path, request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("фото", await AssignmentWorld.ErrorAsync(response));
        Assert.Empty(await world.BlobsAsync());
        if (editing)
        {
            var current = await ReadAsync(client, report);
            Assert.Equal(ReviewWorld.Version(draft!.Value), ReviewWorld.Version(current));
            Assert.Single(current.GetProperty("lines").EnumerateArray());
            Assert.Equal(photo, Assert.Single(await world.PhotosAsync()).Id);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{ReportEndpoints.Path}/{report}")).StatusCode);
            Assert.Empty(await world.PhotosAsync());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Повтор_удаления_не_снимает_заново_объявленное_фото(bool currentVersion)
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo, change) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var draft = await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);
        var url = PhotoWorld.LineUrl(report, line, photo) + $"?changeId={change}";
        var removed = await client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Delete, url, ReviewWorld.Version(draft)));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var again = await DeclareAsync(client, report, line, ReviewWorld.Version(await ReportWorld.JsonAsync(removed)), photo);
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(client, report, line, photo, PhotoWorld.Shot())).StatusCode);
        var journal = (await world.EntriesAsync(report)).Count;
        var files = await world.BlobsAsync();

        var repeat = await client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Delete, url,
            ReviewWorld.Version(currentVersion ? again : draft)));

        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        var body = await ReportWorld.JsonAsync(repeat);
        Assert.True(body.GetProperty("replayed").GetBoolean());
        Assert.Equal(ReviewWorld.Version(again), ReviewWorld.Version(body));
        Assert.Equal(LinePhotoState.Stored, Assert.Single(await world.PhotosAsync()).State);
        Assert.Equal(files.Order(), (await world.BlobsAsync()).Order());
        Assert.Equal(journal, (await world.EntriesAsync(report)).Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Пропавший_файл_при_сохранённом_реестре_даёт_404(bool preview, bool conditional)
    {
        var (client, _, _, photo) = await StoredAsync();
        var url = $"/api/work/photos/{photo}" + (preview ? "/preview" : "");
        var first = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var stored = Assert.Single(await world.PhotosAsync());
        // Убираем только объект: реестр и запись фото сохраняются, как при потере файла в хранилище.
        await host.Services.GetRequiredService<FakeBlobStorage>()
            .DeleteAsync(preview ? stored.PreviewBlobPath! : stored.BlobPath!);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (conditional) request.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.Tag);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(2, (await world.BlobsAsync()).Count);
        Assert.Equal(LinePhotoState.Stored, Assert.Single(await world.PhotosAsync()).State);
    }

    [Fact]
    public async Task Недоступное_хранилище_не_выдаётся_за_отсутствующий_файл()
    {
        var (client, _, _, photo) = await StoredAsync();
        var storage = host.Services.GetRequiredService<FakeBlobStorage>();
        storage.Offline = true;
        try
        {
            Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync($"/api/work/photos/{photo}")).StatusCode);
        }
        finally { storage.Offline = false; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Новая_загрузка_того_же_идентификатора_меняет_ETag(bool preview)
    {
        var (client, report, line, photo) = await StoredAsync();
        var url = $"/api/work/photos/{photo}" + (preview ? "/preview" : "");
        var first = await client.GetAsync(url);
        var oldTag = first.Headers.ETag!.Tag;
        var oldBytes = await first.Content.ReadAsByteArrayAsync();
        var removed = await PhotoWorld.DeleteAsync(client, report, line, photo, ReviewWorld.Version(await ReadAsync(client, report)));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        await DeclareAsync(client, report, line, ReviewWorld.Version(await ReportWorld.JsonAsync(removed)), photo);
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(client, report, line, photo, PhotoSamples.Jpeg(320, 240))).StatusCode);
        using var old = new HttpRequestMessage(HttpMethod.Get, url);
        old.Headers.TryAddWithoutValidation("If-None-Match", oldTag);

        var changed = await client.SendAsync(old);

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(oldTag, changed.Headers.ETag!.Tag);
        var newBytes = await changed.Content.ReadAsByteArrayAsync();
        Assert.False(oldBytes.SequenceEqual(newBytes));
        using var current = new HttpRequestMessage(HttpMethod.Get, url);
        current.Headers.TryAddWithoutValidation("If-None-Match", changed.Headers.ETag.Tag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(current)).StatusCode);
    }

    private async Task<(HttpClient Client, Guid Report, Guid Line, Guid Photo)> StoredAsync()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(client, report, line, photo, PhotoWorld.Shot())).StatusCode);
        return (client, report, line, photo);
    }

    private static async Task<JsonElement> ReadAsync(HttpClient client, Guid report) =>
        await ReportWorld.JsonAsync(await client.GetAsync($"{ReportEndpoints.Path}/{report}"));

    private async Task<JsonElement> DeclareAsync(HttpClient client, Guid report, Guid line, string? version, params Guid[] photos)
    {
        var response = await ReportWorld.PutAsync(client, $"{ReportEndpoints.Path}/{report}", version, new
        {
            id = report, constructionId = world.Reports.SiteA, date = ReportWorld.Day16, hours = 8m,
            changeId = Guid.NewGuid(), lines = new[] { world.Line(line, photos) },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReportWorld.JsonAsync(response);
    }
}
