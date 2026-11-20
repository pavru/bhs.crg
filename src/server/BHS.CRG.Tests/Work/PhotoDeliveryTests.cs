using System.Net;
using System.Text.Json;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Modules.Work.Review;
using BHS.CRG.Tests.Integration;

namespace BHS.CRG.Tests.Work;

/// <summary>
/// Отчёт не ждёт фото (change <c>work-17-photos</c>, ТЗ WORK-34, WORK-28, WORK-11): недогруженные — числом у отчёта и
/// пометкой очереди, подача и приёмка без них проходят; досылка объявленного — доставка, новое фото после подачи —
/// правка после подачи.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class PhotoDeliveryTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly PhotoWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<JsonElement> QueueAsync(HttpClient manager)
    {
        var response = await manager.GetAsync(ReviewQueue.Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReportWorld.JsonAsync(response);
    }

    private static JsonElement? Mark(JsonElement row, string code) =>
        row.GetProperty("marks").EnumerateArray().Cast<JsonElement?>().FirstOrDefault(m => m!.Value.GetProperty("code").GetString() == code);

    /// <summary>
    /// Подача с двумя недогруженными фото проходит, очередь помечает «фото не загружены: 2»; фото догнали — пометка снята,
    /// счётчик очереди уменьшился.
    /// </summary>
    [Fact]
    public async Task Пометка_называет_недогруженные_и_снимается_после_догрузки()
    {
        var (_, installer) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, manager) = await world.Reports.ManagerAsync("Менеджеров Михаил Михайлович", world.Reports.SiteA);
        var (report, line, a, b) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var submitted = await world.SubmittedAsync(installer, report, world.Reports.SiteA, line, a, b);

        Assert.Equal("submitted", submitted.GetProperty("status").GetString());
        Assert.Equal(2, submitted.GetProperty("photosPending").GetInt32());
        var queue = await QueueAsync(manager);
        var row = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal(2, row.GetProperty("photosPending").GetInt32());
        Assert.Equal("фото не загружены: 2", Mark(row, ReviewMarks.PhotosPending)!.Value.GetProperty("text").GetString());
        Assert.Equal(1, queue.GetProperty("counts").GetProperty(ReviewMarks.PhotosPending).GetInt32());

        foreach (var photo in new[] { a, b })
            Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(installer, report, line, photo, PhotoWorld.Shot())).StatusCode);

        var after = await QueueAsync(manager);
        var done = Assert.Single(after.GetProperty("items").EnumerateArray());
        Assert.Equal(0, done.GetProperty("photosPending").GetInt32());
        Assert.Null(Mark(done, ReviewMarks.PhotosPending));
        Assert.Equal(0, after.GetProperty("counts").GetProperty(ReviewMarks.PhotosPending).GetInt32());
    }

    /// <summary>Приёмка при <c>photosPending: 1</c> проходит; пометка остаётся, пока фото не придёт.</summary>
    [Fact]
    public async Task Приёмка_при_недогруженном_фото_проходит()
    {
        var (_, installer) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, manager) = await world.Reports.ManagerAsync("Менеджеров Михаил Михайлович", world.Reports.SiteA);
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.SubmittedAsync(installer, report, world.Reports.SiteA, line, photo);
        var seen = await ReviewWorld.AsManagerAsync(manager, report);

        var accepted = await ReviewWorld.AcceptAsync(manager, report, ReviewWorld.Version(seen));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var body = await ReportWorld.JsonAsync(accepted);
        Assert.Equal("accepted", body.GetProperty("status").GetString());
        Assert.Equal(1, body.GetProperty("photosPending").GetInt32());
        Assert.NotNull(Mark(body, ReviewMarks.PhotosPending));
        // Фото, пришедшее к принятому отчёту, — та же доставка объявленного.
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(installer, report, line, photo, PhotoWorld.Shot())).StatusCode);
        Assert.Equal(0, (await ReviewWorld.AsManagerAsync(manager, report)).GetProperty("photosPending").GetInt32());
    }

    /// <summary>
    /// Фото объявлено до подачи, файл пришёл после неё — доставка: журнал не получает «правки после подачи», пометки
    /// <c>edited-after-submit</c> нет (WORK-11).
    /// </summary>
    [Fact]
    public async Task Досылка_объявленного_фото_не_правка()
    {
        var (_, installer) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, photo) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.SubmittedAsync(installer, report, world.Reports.SiteA, line, photo);
        var before = (await world.EntriesAsync(report)).Count;

        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(installer, report, line, photo, PhotoWorld.Shot())).StatusCode);

        Assert.Equal(before, (await world.EntriesAsync(report)).Count);
        var read = await ReportWorld.JsonAsync(await installer.GetAsync($"{ReportEndpoints.Path}/{report}"));
        Assert.Null(Mark(read, ReviewMarks.EditedAfterSubmit));
    }

    /// <summary>
    /// К поданному отчёту приходит фото, которого строка не объявляла, — правка после подачи: журнал называет фото, отчёт
    /// несёт пометку <c>edited-after-submit</c>. Так же — фото, впервые объявленное правкой после подачи (WORK-11).
    /// </summary>
    [Fact]
    public async Task Новое_фото_после_подачи_правка_после_подачи()
    {
        var (_, installer) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (report, line, declared) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await world.SubmittedAsync(installer, report, world.Reports.SiteA, line, declared);
        var fresh = Guid.NewGuid();

        var upload = await PhotoWorld.UploadAsync(installer, report, line, fresh, PhotoWorld.Shot());

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var entry = (await world.EntriesAsync(report)).Last();
        Assert.Equal(ReportJournalKinds.Updated, entry.Kind);
        Assert.True(entry.AfterSubmission);
        Assert.Contains("\"photos\"", entry.Changes);
        Assert.Contains(fresh.ToString(), entry.Changes);
        var read = await ReportWorld.JsonAsync(await installer.GetAsync($"{ReportEndpoints.Path}/{report}"));
        Assert.NotNull(Mark(read, ReviewMarks.EditedAfterSubmit));
        // Повтор той же загрузки второй записи журнала не даёт.
        var count = (await world.EntriesAsync(report)).Count;
        Assert.Equal(HttpStatusCode.OK, (await PhotoWorld.UploadAsync(installer, report, line, fresh, PhotoWorld.Shot())).StatusCode);
        Assert.Equal(count, (await world.EntriesAsync(report)).Count);

        // Объявленное правкой после подачи — тоже правка после подачи, с номером строки и полем «photos».
        var third = Guid.NewGuid();
        var changed = await ReportWorld.PutAsync(installer, $"{ReportEndpoints.Path}/{report}", ReviewWorld.Version(read), new
        {
            id = report, constructionId = world.Reports.SiteA, date = ReportWorld.Day16, hours = 8m, changeId = Guid.NewGuid(),
            lines = new[] { world.Line(line, declared, fresh, third) },
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var declaredLater = (await world.EntriesAsync(report)).Last();
        Assert.True(declaredLater.AfterSubmission);
        Assert.Contains(third.ToString(), declaredLater.Changes);
    }
}
