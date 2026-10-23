using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;

namespace BHS.CRG.Tests.Plan;

/// <summary>
/// Адреса смет: чтение, правка с версией, ворота модуля и права (change <c>plan-06-estimate-model</c>,
/// ТЗ PLAN-1, PLAN-2, PLAN-4, PLAN-9.1, AUTH-8, AUTH-9).
/// </summary>
[Collection(PlanOnlyCollection.Name)]
public class EstimateEndpointsTests(PlanOnlyHost host)
{
    /// <summary>
    /// Смета с двумя редакциями: у каждой номер, признак фиксации и действующей; строка без вида работы
    /// помечена «не сопоставлена», у строки — материалы и трудозатраты (ТЗ PLAN-1, PLAN-4, PLAN-9.1).
    /// </summary>
    [Fact]
    public async Task Смета_с_двумя_редакциями_и_строки_с_ресурсами_читаются()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        await EstimateSeed.RevisionAsync(host, estimate, await EstimateSeed.MatchedRowAsync(host, site, 10m));
        var matched = await EstimateSeed.MatchedRowAsync(host, site, 20m);
        var second = await EstimateSeed.RevisionAsync(host, estimate,
            matched with { Labor = [1.5m] },
            new EstimateRowDraft("Без вида работы", LsrSectionCode: "1", LsrSectionName: "Электромонтаж",
                Materials: [new("Кабель ВВГнг 3х2,5", 105m, SourceCode: "21.2.01", SourceUnit: "м")]));
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);

        var list = await reader.GetFromJsonAsync<JsonElement>($"/api/plan/estimates?constructionId={site.Construction}");
        var one = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(1, one.GetProperty("currentRevisionNumber").GetInt32());
        Assert.Equal("main", one.GetProperty("kind").GetString());
        var revisions = one.GetProperty("revisions").EnumerateArray().ToList();
        Assert.Equal([1, 2], revisions.Select(r => r.GetProperty("number").GetInt32()));
        Assert.Equal([true, false], revisions.Select(r => r.GetProperty("current").GetBoolean()));
        Assert.All(revisions, r => Assert.Equal(JsonValueKind.Null, r.GetProperty("frozen").ValueKind));
        Assert.Equal(1, revisions[1].GetProperty("unmatched").GetInt32());
        Assert.Equal(2, revisions[1].GetProperty("rows").GetInt32());

        var rows = (await reader.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{second.Id}/rows"))
            .GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Empty(rows[0].GetProperty("marks").EnumerateArray());
        Assert.Equal(1.5m, Assert.Single(rows[0].GetProperty("labor").EnumerateArray()).GetProperty("manHours").GetDecimal());

        var mark = Assert.Single(rows[1].GetProperty("marks").EnumerateArray());
        Assert.Equal("unmatched", mark.GetProperty("code").GetString());
        Assert.Contains("вида работы", mark.GetProperty("text").GetString());
        Assert.Equal("Электромонтаж", rows[1].GetProperty("lsrSection").GetProperty("name").GetString());
        var material = Assert.Single(rows[1].GetProperty("materials").EnumerateArray());
        Assert.Equal(105m, material.GetProperty("quantity").GetDecimal());
        Assert.Equal("м", material.GetProperty("sourceUnit").GetString());
        Assert.Equal("unmatched", Assert.Single(material.GetProperty("marks").EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Строки_читаются_страницами_по_курсору()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var revision = await EstimateSeed.RevisionAsync(host, estimate,
            new EstimateRowDraft("Первая"), new EstimateRowDraft("Вторая"), new EstimateRowDraft("Третья"));
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);

        var first = await reader.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{revision.Id}/rows?limit=2");
        Assert.Equal(["Первая", "Вторая"], first.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        var next = first.GetProperty("next").GetString();

        var second = await reader.GetFromJsonAsync<JsonElement>($"/api/plan/revisions/{revision.Id}/rows?limit=2&cursor={next}");
        Assert.Equal(["Третья"], second.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("next").ValueKind);
    }

    [Fact]
    public async Task Смета_заводится_на_стройку_и_раздел_только_своей_стройки()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var other = await EstimateSeed.SiteAsync(host);
        var editor = await EditorAsync();

        var created = await editor.PostAsJsonAsync("/api/plan/estimates",
            new { constructionId = site.Construction, sectionId = site.Section, name = "ЭОМ", kind = "additional" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("additional", body.GetProperty("kind").GetString());
        Assert.Equal(site.Section, body.GetProperty("sectionId").GetGuid());
        Assert.Empty(body.GetProperty("revisions").EnumerateArray());

        var foreignSection = await editor.PostAsJsonAsync("/api/plan/estimates",
            new { constructionId = site.Construction, sectionId = other.Section, name = "ЭОМ" });
        Assert.Equal(HttpStatusCode.BadRequest, foreignSection.StatusCode);

        var noSite = await editor.PostAsJsonAsync("/api/plan/estimates", new { constructionId = Guid.NewGuid(), name = "ЭОМ" });
        Assert.Equal(HttpStatusCode.BadRequest, noSite.StatusCode);
    }

    /// <summary>Переименование: без версии — 400, устаревшая — 409, актуальная — проходит (ТЗ PLAN-2).</summary>
    [Fact]
    public async Task Переименование_требует_версию()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction, "Доп", EstimateKind.Additional);
        var editor = await EditorAsync();
        var version = await EstimateRevisionTests.VersionAsync(editor, estimate);

        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(editor, estimate, "Без версии", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(editor, estimate, "Основная ЭОМ", version)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(editor, estimate, "Поверх", version)).StatusCode);

        var now = await editor.GetFromJsonAsync<JsonElement>($"/api/plan/estimates/{estimate}");
        Assert.Equal("Основная ЭОМ", now.GetProperty("name").GetString());
        // Вид, не названный в правке, остаётся прежним: переименование не делает дополнительную основной.
        Assert.Equal("additional", now.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Удаляется_только_смета_без_редакций()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var empty = await EstimateSeed.EstimateAsync(host, site.Construction, "Пустая");
        var full = await EstimateSeed.EstimateAsync(host, site.Construction, "С редакцией");
        await EstimateSeed.RevisionAsync(host, full, new EstimateRowDraft("Строка"));
        var editor = await EditorAsync();

        Assert.Equal(HttpStatusCode.Conflict, (await DeleteAsync(editor, full)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DeleteAsync(editor, empty)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await editor.GetAsync($"/api/plan/estimates/{empty}")).StatusCode);
    }

    /// <summary>С одним правом чтения каждый пишущий адрес — 403 (ТЗ PLAN-2, AUTH-9).</summary>
    [Fact]
    public async Task Чтение_без_правки_не_пишет()
    {
        var (estimate, revision) = await EstimateWithRevisionAsync();
        var reader = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead);

        foreach (var request in Writes(estimate, revision, Guid.NewGuid()))
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(request)).StatusCode);
    }

    /// <summary>
    /// «Инженер ИД» модуля не имеет: каждый адрес смет — отказ ворот, чтение тоже (ТЗ AUTH-8). Это
    /// первый адрес под воротами <c>/api/plan</c> — проверка, которой не было у пустой группы.
    /// </summary>
    [Fact]
    public async Task Инженер_ИД_получает_отказ_ворот_на_каждом_адресе()
    {
        var (estimate, revision) = await EstimateWithRevisionAsync();
        var engineer = await AccessProbe.SignInAsync(host, SystemRoles.IdEngineer, "plan06");

        HttpRequestMessage[] reads =
        [
            new(HttpMethod.Get, $"/api/plan/estimates?constructionId={Guid.NewGuid()}"),
            new(HttpMethod.Get, $"/api/plan/estimates/{estimate}"),
            new(HttpMethod.Get, $"/api/plan/revisions/{revision}/rows"),
        ];
        foreach (var request in reads.Concat(Writes(estimate, revision, Guid.NewGuid())))
            Assert.Equal(HttpStatusCode.Forbidden, (await engineer.SendAsync(request)).StatusCode);
    }

    private async Task<(Guid Estimate, Guid Revision)> EstimateWithRevisionAsync()
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        return (estimate, (await EstimateSeed.RevisionAsync(host, estimate, new EstimateRowDraft("Строка"))).Id);
    }

    /// <summary>Все пишущие адреса смет — с заголовком версии, чтобы отказ был от прав, а не от версии.</summary>
    private static IEnumerable<HttpRequestMessage> Writes(Guid estimate, Guid revision, Guid construction)
    {
        HttpRequestMessage With(HttpMethod method, string url, object? body = null)
        {
            var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("If-Match", "1");
            return request;
        }

        yield return With(HttpMethod.Post, "/api/plan/estimates", new { constructionId = construction, name = "Новая" });
        yield return With(HttpMethod.Put, $"/api/plan/estimates/{estimate}", new { name = "Другое" });
        yield return With(HttpMethod.Delete, $"/api/plan/estimates/{estimate}");
        yield return With(HttpMethod.Post, $"/api/plan/estimates/{estimate}/current-revision", new { revisionId = revision });
        yield return With(HttpMethod.Delete, $"/api/plan/revisions/{revision}");
    }

    private Task<HttpClient> EditorAsync() =>
        GrantedSignIn.WithPermissionsAsync(host, PlanModule.EstimateRead, PlanModule.EstimateEdit);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid estimate, string name, string? version)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/plan/estimates/{estimate}")
        {
            Content = JsonContent.Create(new { name }),
        };
        if (version is not null) request.Headers.TryAddWithoutValidation("If-Match", version);
        return client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, Guid estimate)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/plan/estimates/{estimate}");
        request.Headers.TryAddWithoutValidation("If-Match", await EstimateRevisionTests.VersionAsync(client, estimate));
        return await client.SendAsync(request);
    }
}
