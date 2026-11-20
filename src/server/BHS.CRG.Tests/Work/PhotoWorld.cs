using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Tests.Generation;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Work;

/// <summary>
/// Мир фото строки (change <c>work-17-photos</c>): отчёты с объявленными фото, загрузка файла и то, что легло в базу и в
/// хранилище. Отчёты заводятся адресами, файлы проверяются по реестру блобов ядра — владелец <c>work</c>.
/// </summary>
internal sealed class PhotoWorld(AssignmentHost host)
{
    public ReviewWorld Review { get; } = new(host);

    public ReportWorld Reports => Review.Reports;

    public Task SeedAsync() => Reports.SeedAsync();

    /// <summary>Тело черновика с одной строкой, которая объявляет фото <paramref name="photos" />.</summary>
    public object Draft(Guid id, Guid site, Guid lineId, params Guid[] photos) => new
    {
        id, constructionId = site, date = ReportWorld.Day16, shift = "day", hours = 8m,
        lines = new[] { Line(lineId, photos) },
    };

    /// <summary>Строка тела с перечнем фото.</summary>
    public object Line(Guid lineId, params Guid[] photos) => new
    {
        id = lineId, workTypeId = Reports.WorkType, volume = 120m, unitId = Reports.Unit, location = "оси 3–4",
        photos = photos.Select(p => new { id = p }).ToArray(),
    };

    /// <summary>Завести свой черновик; ответ заведения.</summary>
    public async Task<JsonElement> DraftAsync(HttpClient installer, Guid id, Guid site, Guid lineId, params Guid[] photos)
    {
        var created = await installer.PostAsJsonAsync(ReportEndpoints.Path, Draft(id, site, lineId, photos));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return await ReportWorld.JsonAsync(created);
    }

    /// <summary>Свой отчёт, поданный в 18:00 16.11, со строкой, объявившей фото; ответ подачи.</summary>
    public async Task<JsonElement> SubmittedAsync(HttpClient installer, Guid id, Guid site, Guid lineId, params Guid[] photos)
    {
        var draft = await DraftAsync(installer, id, site, lineId, photos);
        var submitted = await SubmissionTests.SubmitAsync(installer, $"{ReportEndpoints.Path}/{id}", ReviewWorld.Version(draft),
            ReviewWorld.Evening16);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        return await ReportWorld.JsonAsync(submitted);
    }

    public static string LineUrl(Guid report, Guid line, Guid photo) =>
        $"{ReportEndpoints.Path}/{report}/lines/{line}/photos/{photo}";

    /// <summary>Загрузить файл фото — тело запроса сам файл, без <c>If-Match</c>.</summary>
    public static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid report, Guid line, Guid photo, byte[] file)
    {
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return client.PutAsync(LineUrl(report, line, photo), content);
    }

    public static Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid report, Guid line, Guid photo, string? version) =>
        client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Delete, LineUrl(report, line, photo), version));

    /// <summary>Небольшой снимок 800×600 — проходит любой предел.</summary>
    public static byte[] Shot() => PhotoSamples.Jpeg(800, 600);

    /// <summary>Записи фото, как они лежат в базе.</summary>
    public async Task<List<LinePhoto>> PhotosAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Set<LinePhoto>().AsNoTracking().ToListAsync();
    }

    /// <summary>Файлы модуля <c>work</c> в реестре блобов ядра — всё, что модуль положил в хранилище.</summary>
    public async Task<List<string>> BlobsAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().BlobRegistry.AsNoTracking()
            .Where(e => e.Owner == WorkModule.ModuleCode).Select(e => e.Path).ToListAsync();
    }

    public async Task<List<ReportJournalEntry>> EntriesAsync(Guid report) => await Review.EntriesAsync(report);
}
