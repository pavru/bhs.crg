using System.Net;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Photos;
using BHS.CRG.Tests.Integration;

namespace BHS.CRG.Tests.Work;

/// <summary>
/// Выдача фото строки только адресом модуля под изоляцией отчёта (change <c>work-17-photos</c>, ТЗ WORK-39, WORK-54):
/// кому отчёт не виден — 404 как на несуществующее фото, а не 403; содержимое черновика — только автору.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class PhotoAccessTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly PhotoWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Original(Guid photo) => $"/api/work/photos/{photo}";

    private static string Preview(Guid photo) => $"/api/work/photos/{photo}/preview";

    /// <summary>Поданный отчёт монтажника Б с загруженным фото; монтажник Б и номер фото.</summary>
    private async Task<(HttpClient Owner, Guid Photo)> StoredAsync(Guid site)
    {
        var (_, owner) = await world.Reports.InstallerAsync("Сидоров Семён Семёнович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.SubmittedAsync(owner, report, site, line, photo);
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(owner, report, line, photo, PhotoWorld.Shot())).StatusCode);
        return (owner, photo);
    }

    /// <summary>Монтажник А по идентификатору фото из отчёта Б получает тот же 404, что на несуществующее — оба адреса.</summary>
    [Fact]
    public async Task Чужой_монтажник_получает_404_на_оба_адреса()
    {
        var (owner, photo) = await StoredAsync(world.Reports.SiteA);
        var (_, stranger) = await world.Reports.InstallerAsync("Петров Пётр Петрович");

        var original = await stranger.GetAsync(Original(photo));
        var preview = await stranger.GetAsync(Preview(photo));
        var missing = await stranger.GetAsync(Original(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, original.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.Equal(await missing.Content.ReadAsStringAsync(), await original.Content.ReadAsStringAsync());
        Assert.Equal(LinePhotos.NotFoundText, await AssignmentWorld.ErrorAsync(preview));
        // А хозяин отчёта своё фото открывает.
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(Original(photo))).StatusCode);
    }

    /// <summary>Менеджер стройки открывает превью фото поданного отчёта, оригинал — отдельным адресом.</summary>
    [Fact]
    public async Task Менеджер_своей_стройки_получает_превью_и_оригинал()
    {
        var (_, photo) = await StoredAsync(world.Reports.SiteA);
        var (_, manager) = await world.Reports.ManagerAsync("Менеджеров Михаил Михайлович", world.Reports.SiteA);
        var (_, foreign) = await world.Reports.ManagerAsync("Чужов Чеслав Чеславович", world.Reports.SiteB);

        var preview = await manager.GetAsync(Preview(photo));
        var original = await manager.GetAsync(Original(photo));

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("image/jpeg", preview.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.True((await original.Content.ReadAsByteArrayAsync()).Length > (await preview.Content.ReadAsByteArrayAsync()).Length,
            "превью не меньше оригинала — отдан один и тот же файл");
        // Менеджер другой стройки отчёта не видит — и фото тоже.
        Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(Preview(photo))).StatusCode);
    }

    /// <summary>Фото черновика: менеджер стройки получает 404 — содержимое черновика видно после подачи (WORK-54).</summary>
    [Fact]
    public async Task Фото_черновика_неавтору_404()
    {
        var (_, owner) = await world.Reports.InstallerAsync("Сидоров Семён Семёнович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.DraftAsync(owner, report, world.Reports.SiteA, line, photo);
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(owner, report, line, photo, PhotoWorld.Shot())).StatusCode);
        var (_, manager) = await world.Reports.ManagerAsync("Менеджеров Михаил Михайлович", world.Reports.SiteA);

        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(Preview(photo))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(Original(photo))).StatusCode);
        // И загрузить к чужому черновику менеджер не может: его содержимое ему не видно.
        Assert.Equal(HttpStatusCode.NotFound,
            (await PhotoWorld.UploadAsync(manager, report, line, Guid.NewGuid(), PhotoWorld.Shot())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(Preview(photo))).StatusCode);
    }

    /// <summary>Руководитель с правом чтения всех отчётов фото поданного отчёта открывает (WORK-39, WORK-45).</summary>
    [Fact]
    public async Task Читающий_все_отчёты_открывает_фото()
    {
        var (_, photo) = await StoredAsync(world.Reports.SiteB);
        var (_, chief) = await world.Reports.Assignments.UserAsync("Руководитель Р. Р.", WorkModule.ReportRead);

        Assert.Equal(HttpStatusCode.OK, (await chief.GetAsync(Preview(photo))).StatusCode);
    }

    /// <summary>Фото объявлено, файл не дошёл: 404 с причиной «ещё не загружено», а не пустой файл.</summary>
    [Fact]
    public async Task Недогруженное_фото_называет_причину()
    {
        var (_, owner) = await world.Reports.InstallerAsync("Сидоров Семён Семёнович");
        var photo = Guid.NewGuid();
        await world.SubmittedAsync(owner, Guid.NewGuid(), world.Reports.SiteA, Guid.NewGuid(), photo);

        var response = await owner.GetAsync(Original(photo));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(LinePhotos.NotStored, await AssignmentWorld.ErrorAsync(response));
    }

    /// <summary>Повторный запрос с тем же <c>If-None-Match</c> — 304 без тела, но после проверки доступа.</summary>
    [Fact]
    public async Task Повторный_запрос_с_меткой_получает_304_только_при_доступе()
    {
        var (owner, photo) = await StoredAsync(world.Reports.SiteA);
        var first = await owner.GetAsync(Preview(photo));
        var tag = first.Headers.ETag!.Tag;
        var (_, stranger) = await world.Reports.InstallerAsync("Петров Пётр Петрович");

        using var mine = new HttpRequestMessage(HttpMethod.Get, Preview(photo));
        mine.Headers.TryAddWithoutValidation("If-None-Match", tag);
        using var theirs = new HttpRequestMessage(HttpMethod.Get, Preview(photo));
        theirs.Headers.TryAddWithoutValidation("If-None-Match", tag);

        Assert.Equal(HttpStatusCode.NotModified, (await owner.SendAsync(mine)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.SendAsync(theirs)).StatusCode);
    }
}
