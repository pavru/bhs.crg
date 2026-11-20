using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Modules.Work.Photos;
using BHS.CRG.Tests.Generation;
using BHS.CRG.Tests.Integration;
using SkiaSharp;

namespace BHS.CRG.Tests.Work;

/// <summary>
/// Загрузка и удаление фото строки (change <c>work-17-photos</c>, ТЗ WORK-33, WORK-55, MOB-15, AUTH-8): повтор не
/// дублирует, лимиты — до записи в хранилище, загрузка без версии, удаление — с версией.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class PhotoUploadTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly PhotoWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Ответ на загрузку потерялся, клиент повторил тот же <c>PUT</c> — одно фото, тот же ответ (MOB-15).</summary>
    [Fact]
    public async Task Повтор_загрузки_оставляет_одно_фото_и_тот_же_ответ()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var draft = await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);
        Assert.Equal(1, draft.GetProperty("photosPending").GetInt32());
        Assert.Equal("pending", draft.GetProperty("lines")[0].GetProperty("photos")[0].GetProperty("state").GetString());

        var first = await PhotoWorld.UploadAsync(client, report, line, photo, PhotoWorld.Shot());
        var again = await PhotoWorld.UploadAsync(client, report, line, photo, PhotoWorld.Shot());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await again.Content.ReadAsStringAsync());
        var stored = Assert.Single(await world.PhotosAsync());
        Assert.Equal((photo, LinePhotoState.Stored), (stored.Id, stored.State));
        // Снимок и превью — и ни одного файла от повтора.
        Assert.Equal(2, (await world.BlobsAsync()).Count);
        var read = await ReportWorld.JsonAsync(await client.GetAsync($"{ReportEndpoints.Path}/{report}"));
        Assert.Equal(0, read.GetProperty("photosPending").GetInt32());
        // Доставка объявленного фото отчёт не меняет: версия та же, что у заведения.
        Assert.Equal(ReviewWorld.Version(draft), ReviewWorld.Version(read));
    }

    /// <summary>Идентификатор занят фото другой строки — 409 «идентификатор фото занят», ни одно фото не меняется.</summary>
    [Fact]
    public async Task Чужой_идентификатор_фото_отказ()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var photo = Guid.NewGuid();
        var (first, firstLine) = (Guid.NewGuid(), Guid.NewGuid());
        await world.DraftAsync(client, first, world.Reports.SiteA, firstLine, photo);
        var (second, secondLine) = (Guid.NewGuid(), Guid.NewGuid());
        await world.DraftAsync(client, second, world.Reports.SiteB, secondLine);

        var (_, other) = await world.Reports.InstallerAsync("Сидоров Семён Семёнович");

        var upload = await PhotoWorld.UploadAsync(client, second, secondLine, photo, PhotoWorld.Shot());
        var declare = await other.PostAsJsonAsync(ReportEndpoints.Path,
            world.Draft(Guid.NewGuid(), world.Reports.SiteA, Guid.NewGuid(), photo));

        Assert.Equal(HttpStatusCode.Conflict, upload.StatusCode);
        Assert.Contains("фото уже занят", await AssignmentWorld.ErrorAsync(upload));
        Assert.Equal(HttpStatusCode.Conflict, declare.StatusCode);
        Assert.Contains("фото уже занят", await AssignmentWorld.ErrorAsync(declare));
        var only = Assert.Single(await world.PhotosAsync());
        Assert.Equal((firstLine, LinePhotoState.Pending), (only.LineId, only.State));
        Assert.Empty(await world.BlobsAsync());
    }

    /// <summary>Файл больше предела настройки (15 МБ) — 413 с пределом, и в хранилище не легло ничего (WORK-33).</summary>
    [Fact]
    public async Task Файл_сверх_предела_отвергается_до_хранилища()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);

        var response = await PhotoWorld.UploadAsync(client, report, line, photo, new byte[15 * 1024 * 1024 + 1]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("Фото больше 15 МБ", await AssignmentWorld.ErrorAsync(response));
        Assert.Empty(await world.BlobsAsync());
        Assert.Equal(LinePhotoState.Pending, Assert.Single(await world.PhotosAsync()).State);
    }

    /// <summary>Фото сверх числа на строку — 400 с пределом: и объявленное правкой, и пришедшее без объявления (WORK-33).</summary>
    [Fact]
    public async Task Фото_сверх_числа_отвергается()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line) = (Guid.NewGuid(), Guid.NewGuid());
        var ten = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        await world.DraftAsync(client, report, world.Reports.SiteA, line, ten);

        var upload = await PhotoWorld.UploadAsync(client, report, line, Guid.NewGuid(), PhotoWorld.Shot());
        var eleven = await client.PostAsJsonAsync(ReportEndpoints.Path,
            world.Draft(Guid.NewGuid(), world.Reports.SiteB, Guid.NewGuid(), [.. Enumerable.Range(0, 11).Select(_ => Guid.NewGuid())]));

        Assert.Equal(HttpStatusCode.BadRequest, upload.StatusCode);
        Assert.Contains("Не больше 10 фото на строку", await AssignmentWorld.ErrorAsync(upload));
        Assert.Equal(HttpStatusCode.BadRequest, eleven.StatusCode);
        Assert.Contains("не больше 10 фото на строку", await AssignmentWorld.ErrorAsync(eleven));
        Assert.Equal(10, (await world.PhotosAsync()).Count);
        Assert.Empty(await world.BlobsAsync());
    }

    /// <summary>
    /// Снимок «набок» с координатами (WORK-55): выданное фото и превью повёрнуты как сняты, а геометка и время съёмки
    /// сохранены у фото данными — порт фото (work-05) метаданных в файл не переносит.
    /// </summary>
    [Fact]
    public async Task Поворот_применён_геометка_сохранена()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);
        var shot = PhotoSamples.WithExif(PhotoSamples.Jpeg(3200, 2400),
            PhotoSamples.Exif(orientation: 6, geo: PhotoSamples.Place, takenAt: "2026:11:16 10:20:30", offset: "+03:00"));

        var upload = await PhotoWorld.UploadAsync(client, report, line, photo, shot);

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var body = await ReportWorld.JsonAsync(upload);
        Assert.Equal((LinePhotos.MaxSide * 3 / 4, LinePhotos.MaxSide), (body.GetProperty("width").GetInt32(), body.GetProperty("height").GetInt32()));
        var stored = Assert.Single(await world.PhotosAsync());
        Assert.Equal(PhotoSamples.Place.Lat, stored.GeoLat!.Value, 4);
        Assert.Equal(PhotoSamples.Place.Lon, stored.GeoLon!.Value, 4);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 10, 20, 30, TimeSpan.FromHours(3)), stored.TakenAt);

        using var original = SKBitmap.Decode(await client.GetByteArrayAsync($"/api/work/photos/{photo}"));
        using var preview = SKBitmap.Decode(await client.GetByteArrayAsync($"/api/work/photos/{photo}/preview"));
        Assert.True(original.Height > original.Width, $"оригинал не повёрнут: {original.Width}×{original.Height}");
        Assert.True(preview.Height > preview.Width && preview.Height == LinePhotos.PreviewSide,
            $"превью не повёрнуто или не уменьшено: {preview.Width}×{preview.Height}");
    }

    /// <summary>
    /// Отчёт поправили после того, как фото встало в очередь, — фото приходит без <c>If-Match</c> и принимается: версия
    /// отчёта к приходу фото сдвинута почти всегда.
    /// </summary>
    [Fact]
    public async Task Фото_после_правки_отчёта_принимается_без_версии()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var draft = await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);
        var changed = await ReportWorld.PutAsync(client, $"{ReportEndpoints.Path}/{report}", ReviewWorld.Version(draft), new
        {
            id = report, constructionId = world.Reports.SiteA, date = ReportWorld.Day16, hours = 9m, changeId = Guid.NewGuid(),
            lines = new[] { world.Line(line, photo) },
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        var upload = await PhotoWorld.UploadAsync(client, report, line, photo, PhotoWorld.Shot());

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Assert.Equal(LinePhotoState.Stored, Assert.Single(await world.PhotosAsync()).State);
    }

    /// <summary>
    /// Удаление стирает данные — версия обязательна: без заголовка 400, с устаревшей 409, фото на месте. С текущей —
    /// фото снято, его файлы удалены.
    /// </summary>
    [Fact]
    public async Task Удаление_фото_требует_версию_отчёта()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var draft = await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(client, report, line, photo, PhotoWorld.Shot())).StatusCode);
        var stale = ReviewWorld.Version(draft);
        var moved = await ReportWorld.PutAsync(client, $"{ReportEndpoints.Path}/{report}", stale, new
        {
            id = report, constructionId = world.Reports.SiteA, date = ReportWorld.Day16, hours = 7m, changeId = Guid.NewGuid(),
            lines = new[] { world.Line(line, photo) },
        });
        var current = ReviewWorld.Version(await ReportWorld.JsonAsync(moved));

        var bare = await PhotoWorld.DeleteAsync(client, report, line, photo, version: null);
        var old = await PhotoWorld.DeleteAsync(client, report, line, photo, stale);

        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
        Assert.Equal(ReportDesk.Stale, await AssignmentWorld.ErrorAsync(old));
        Assert.Single(await world.PhotosAsync());
        Assert.Equal(2, (await world.BlobsAsync()).Count);

        var done = await PhotoWorld.DeleteAsync(client, report, line, photo, current);

        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Empty((await ReportWorld.JsonAsync(done)).GetProperty("lines")[0].GetProperty("photos").EnumerateArray());
        Assert.Empty(await world.PhotosAsync());
        Assert.Empty(await world.BlobsAsync());
    }

    /// <summary>Строка, снявшая фото правкой, уносит и его файлы — ссылка уходит первой, файл после фиксации.</summary>
    [Fact]
    public async Task Снятое_правкой_фото_уносит_файлы()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var draft = await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(client, report, line, photo, PhotoWorld.Shot())).StatusCode);

        var changed = await ReportWorld.PutAsync(client, $"{ReportEndpoints.Path}/{report}", ReviewWorld.Version(draft), new
        {
            id = report, constructionId = world.Reports.SiteA, date = ReportWorld.Day16, hours = 8m, changeId = Guid.NewGuid(),
            lines = new[] { world.Line(line) },
        });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Empty(await world.PhotosAsync());
        Assert.Empty(await world.BlobsAsync());
    }

    /// <summary>
    /// Правка без перечня фото их не теряет: клиент, не знающий о фото (правка шапки, экран без галереи), не снимает
    /// прикреплённое.
    /// </summary>
    [Fact]
    public async Task Правка_без_перечня_фото_их_не_трогает()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var draft = await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);

        var changed = await ReportWorld.PutAsync(client, $"{ReportEndpoints.Path}/{report}", ReviewWorld.Version(draft),
            world.Reports.Draft(report, world.Reports.SiteA, hours: 6m, changeId: Guid.NewGuid(), lineId: line));

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(photo, Assert.Single(await world.PhotosAsync()).Id);
    }

    /// <summary>Только право чтения всех отчётов — правки фото не даёт: 403 на загрузку и на удаление (AUTH-8).</summary>
    [Fact]
    public async Task Руководитель_фото_не_загружает()
    {
        var (_, installer) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var submitted = await world.SubmittedAsync(installer, report, world.Reports.SiteA, line, photo);
        var (_, chief) = await world.Reports.Assignments.UserAsync("Руководитель Р. Р.", WorkModule.ReportRead);

        var upload = await PhotoWorld.UploadAsync(chief, report, line, photo, PhotoWorld.Shot());
        var delete = await PhotoWorld.DeleteAsync(chief, report, line, photo, ReviewWorld.Version(submitted));

        Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(LinePhotoState.Pending, Assert.Single(await world.PhotosAsync()).State);
    }

    /// <summary>Не изображение под видом фото — 400 «не распознан как изображение», в хранилище пусто.</summary>
    [Fact]
    public async Task Не_изображение_отвергается()
    {
        var (_, client) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.DraftAsync(client, report, world.Reports.SiteA, line, photo);

        var response = await PhotoWorld.UploadAsync(client, report, line, photo, "%PDF-1.4 не фото"u8.ToArray());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("не распознан как изображение", await AssignmentWorld.ErrorAsync(response));
        Assert.Empty(await world.BlobsAsync());
    }
}
