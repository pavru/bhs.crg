using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Адреса импорта: выбор цели при загрузке, отмена с версией, ворота модуля и право правки (change
/// <c>plan-07-estimate-import</c>, PR 1; ТЗ PLAN-2, PLAN-7, AUTH-8, решение В-12).
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class EstimateImportEndpointsTests(PlanOnlyHost host)
{
    private const string Lsr = "lsr-16.2.0-vn";

    /// <summary>Куда ляжет редакция — ровно одно из двух, и только в своей стройке (решение В-12 (а)).</summary>
    [Fact]
    public async Task Загрузка_требует_одну_цель_своей_стройки()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var other = await EstimateSeed.SiteAsync(host, "Чужая");
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var foreign = await EstimateSeed.EstimateAsync(host, other.Construction);
        var client = await EstimateImportFlow.EditorAsync(host);
        var file = GrandSmetaSamples.Gsfx(Lsr);

        (MultipartFormDataContent Form, string Words)[] refused =
        [
            (EstimateImportFlow.Form(file, "a.gsfx", site.Construction), "Не выбрано, куда"),
            (EstimateImportFlow.Form(file, "a.gsfx", site.Construction, estimateId: estimate, newName: "Ещё"), "не то и другое"),
            (EstimateImportFlow.Form(file, "a.gsfx", null, newName: "Новая"), "Не названа стройка"),
            (EstimateImportFlow.Form(file, "a.gsfx", site.Construction, estimateId: foreign), "другой стройки"),
            (EstimateImportFlow.Form(file, "a.gsfx", site.Construction, estimateId: estimate, sectionId: site.Section), "только у новой сметы"),
            (EstimateImportFlow.Form(file, "a.gsfx", site.Construction, estimateId: estimate, kind: "additional"), "только у новой сметы"),
            (EstimateImportFlow.Form(file, "a.gsfx", site.Construction, newName: "Новая", sectionId: other.Section), "Раздела нет"),
            (EstimateImportFlow.Form(file, "a.gsfx", site.Construction, newName: "Новая", kind: "лишняя"), "неизвестен"),
            (EstimateImportFlow.Form([], "a.gsfx", site.Construction, newName: "Новая"), "Файл пуст"),
        ];
        foreach (var (form, words) in refused)
            using (form)
            {
                var response = await client.PostAsync("/api/plan/imports", form);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Contains(words, await response.Content.ReadAsStringAsync());
            }

        using var scope = host.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateImports
            .AnyAsync(i => i.ConstructionId == site.Construction || i.ConstructionId == other.Construction));
    }

    /// <summary>Новая смета на раздел — раздел и вид запоминаются у импорта до создания редакции.</summary>
    [Fact]
    public async Task Новая_смета_на_раздел_запоминается_у_импорта()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.EditorAsync(host);

        var uploaded = await EstimateImportFlow.UploadAsync(client, EstimateImportFlow.Form(GrandSmetaSamples.Gsfx(Lsr),
            Lsr + ".gsfx", site.Construction, newName: "  Доп. ЭОМ  ", sectionId: site.Section, kind: "additional"));

        var target = uploaded.GetProperty("target");
        Assert.Equal(JsonValueKind.Null, target.GetProperty("estimateId").ValueKind);
        var created = target.GetProperty("newEstimate");
        Assert.Equal("Доп. ЭОМ", created.GetProperty("name").GetString());
        Assert.Equal(site.Section, created.GetProperty("sectionId").GetGuid());
        Assert.Equal("additional", created.GetProperty("kind").GetString());
        Assert.Equal(Lsr + ".gsfx", uploaded.GetProperty("fileName").GetString());
        await EstimateImportFlow.OutcomeAsync(client, uploaded);
    }

    /// <summary>
    /// Отмена — только с версией записи импорта: без неё 400, с устаревшей 409. Отменённый импорт уходит вместе с
    /// файлом (ТЗ PLAN-7).
    /// </summary>
    [Fact]
    public async Task Отмена_требует_версию_и_убирает_файл()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var client = await EstimateImportFlow.EditorAsync(host);
        var uploaded = await EstimateImportFlow.UploadAsync(client, site.Construction, Lsr);
        var stale = uploaded.GetProperty("version").GetString();
        var report = await EstimateImportFlow.OutcomeAsync(client, uploaded);
        var id = EstimateImportFlow.Id(report);
        var path = await BlobPathAsync(id);
        Assert.NotNull(await BlobsSizeAsync(path));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(EstimateImportFlow.Delete(id, null))).StatusCode);
        // Разбор записал исход — версия, полученная при загрузке, устарела.
        Assert.NotEqual(stale, report.GetProperty("version").GetString());
        var conflict = await client.SendAsync(EstimateImportFlow.Delete(id, stale));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("Перечитайте отчёт импорта", await conflict.Content.ReadAsStringAsync());

        var deleted = await client.SendAsync(EstimateImportFlow.Delete(id, report.GetProperty("version").GetString()));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plan/imports/{id}")).StatusCode);
        Assert.Null(await BlobsSizeAsync(path));
    }

    /// <summary>
    /// Список импортов стройки — то, чем находят несостоявшийся импорт: он держит стройку и раздел новой сметы,
    /// пока его не отменят. С <c>estimateId</c> — только импорты в эту смету; без стройки — 400.
    /// </summary>
    [Fact]
    public async Task Список_импортов_стройки_находит_несостоявшийся()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var other = await EstimateSeed.SiteAsync(host, "Соседняя");
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var client = await EstimateImportFlow.EditorAsync(host);
        var intoNew = EstimateImportFlow.Id(await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form(GrandSmetaSamples.Gsfx(Lsr), "новая.gsfx", site.Construction, newName: "Новая", sectionId: site.Section))));
        var intoEstimate = EstimateImportFlow.Id(await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form(GrandSmetaSamples.Gsfx(Lsr), "в смету.gsfx", site.Construction, estimateId: estimate))));
        await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client, other.Construction, Lsr));

        var all = await client.GetFromJsonAsync<JsonElement>($"/api/plan/imports?constructionId={site.Construction}");
        Assert.Equal([intoEstimate, intoNew], all.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        var item = all.EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == intoNew);
        Assert.Equal("новая.gsfx", item.GetProperty("fileName").GetString());
        Assert.Equal("parsed", item.GetProperty("state").GetString());
        Assert.Equal(site.Section, item.GetProperty("target").GetProperty("newEstimate").GetProperty("sectionId").GetGuid());
        Assert.False(string.IsNullOrEmpty(item.GetProperty("version").GetString()));

        var only = await client.GetFromJsonAsync<JsonElement>(
            $"/api/plan/imports?constructionId={site.Construction}&estimateId={estimate}");
        var single = Assert.Single(only.EnumerateArray());
        Assert.Equal("Основная", single.GetProperty("target").GetProperty("estimateName").GetString());

        // Версия из списка годится для отмены: найденный импорт отменяется прямо по нему.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(EstimateImportFlow.Delete(intoNew, item.GetProperty("version").GetString()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/plan/imports")).StatusCode);
    }

    /// <summary>Удалённая смета без редакций уносит несостоявшиеся импорты в неё — и их файлы.</summary>
    [Fact]
    public async Task Удаление_сметы_уносит_её_импорты()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var client = await EstimateImportFlow.EditorAsync(host);
        var report = await EstimateImportFlow.OutcomeAsync(client, await EstimateImportFlow.UploadAsync(client,
            EstimateImportFlow.Form(GrandSmetaSamples.Gsfx(Lsr), Lsr + ".gsfx", site.Construction, estimateId: estimate)));
        var path = await BlobPathAsync(EstimateImportFlow.Id(report));

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/plan/estimates/{estimate}");
        request.Headers.TryAddWithoutValidation("If-Match", await EstimateRevisionTests.VersionAsync(client, estimate));
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(request)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/plan/imports/{EstimateImportFlow.Id(report)}")).StatusCode);
        Assert.Null(await BlobsSizeAsync(path));
    }

    /// <summary>С одним чтением смет импорт не загрузить, не прочитать и не отменить (ТЗ PLAN-2).</summary>
    [Fact]
    public async Task Без_права_правки_смет_отказ()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var import = await UploadedAsync(site);
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, PlanModule.EstimateCost);

        foreach (var request in Requests(import, site.Construction))
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(request)).StatusCode);
    }

    /// <summary>«Инженер ИД» модуля не имеет: каждый адрес импорта — отказ ворот (ТЗ AUTH-8).</summary>
    [Fact]
    public async Task Инженер_ИД_получает_отказ_ворот_на_каждом_адресе()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var import = await UploadedAsync(site);
        var engineer = await AccessProbe.SignInAsync(host, SystemRoles.IdEngineer, "plan07");

        foreach (var request in Requests(import, site.Construction))
            Assert.Equal(HttpStatusCode.Forbidden, (await engineer.SendAsync(request)).StatusCode);
    }

    private async Task<Guid> UploadedAsync(EstimateSeed.Site site)
    {
        var client = await EstimateImportFlow.EditorAsync(host);
        return EstimateImportFlow.Id(await EstimateImportFlow.OutcomeAsync(client,
            await EstimateImportFlow.UploadAsync(client, site.Construction, Lsr)));
    }

    /// <summary>Все адреса импорта; пишущие — с версией, чтобы отказ был от прав, а не от неё.</summary>
    private static IEnumerable<HttpRequestMessage> Requests(Guid import, Guid construction)
    {
        yield return new HttpRequestMessage(HttpMethod.Post, "/api/plan/imports")
        {
            Content = EstimateImportFlow.Form(GrandSmetaSamples.Gsfx(Lsr), Lsr + ".gsfx", construction, newName: "Чужая"),
        };
        yield return new HttpRequestMessage(HttpMethod.Get, $"/api/plan/imports?constructionId={construction}");
        yield return new HttpRequestMessage(HttpMethod.Get, $"/api/plan/imports/{import}");
        yield return EstimateImportFlow.Delete(import, "1");
    }

    private async Task<string> BlobPathAsync(Guid import)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PlanDbContext>().EstimateImports
            .Where(i => i.Id == import).Select(i => i.BlobPath).SingleAsync();
    }

    private async Task<long?> BlobsSizeAsync(string path)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredKeyedService<IModuleBlobs>(PlanModule.ModuleCode).SizeAsync(path);
    }
}
