using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Application.Documents;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Modules.Work.Review;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

[Collection(AssignmentHost.Collection)]
public class ReviewRequiredMissingTests(AssignmentHost host) : IAsyncLifetime
{
    private const string ReportPath = ReportEndpoints.Path;
    private const string Required = ReviewMarks.RequiredMissing;
    private const string Customer = ReviewMarks.FieldsMissing;
    private readonly WorkPeriodWorld period = new(host);
    private readonly WorkTypeSeed seed = new(host);
    private ReportWorld World => period.World;

    public Task InitializeAsync() => period.Review.SeedAsync();

    public Task DisposeAsync()
    {
        host.ReportTime = null;
        host.ClosureTime = null;
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Автоподача_частичная_правка_и_актуальные_требования()
    {
        await MarkCustomerFieldAsync();
        var both = await BothAsync();
        var materialType = await seed.TypeAsync("НоменклатураТест", "Номенклатура");
        var material = await seed.RecordAsync(materialType.Id, "Лоток 100х50");
        var (_, installer) = await World.InstallerAsync("Монтажник");
        var (_, manager) = await World.ManagerAsync("Менеджер", World.SiteA);
        var (id, first, optional, last) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        object Optional() => Line(optional, World.WorkType);
        object First(string? location = null) => Line(first, World.WorkTypeNeedsPlace, location);
        object Last(string? location = null, Guid? nomenclature = null) => Line(last, both, location, nomenclature);
        await CreateAsync(installer, Body(id, World.SiteA, [First(" \t"), Optional(), Last()]));
        await period.Review.TickAsync(ReviewWorld.Up19);
        host.ReportTime = ReviewWorld.Up19.ToUniversalTime();

        var expected = new[] { (1, first, "location"), (3, last, "location"), (3, last, "materials") };
        var queue = await ReadQueueAsync(manager, $"?mark={Required}");
        Assert.Equal(1, Count(queue, Required));
        Assert.Equal(1, Count(queue, Customer));
        var row = Assert.Single(queue.GetProperty("items").EnumerateArray());
        Assert.Equal("не указаны обязательные место работ и материалы", MarkText(row, Required));
        var card = await ReadCardAsync(manager, id);
        Assert.Equal(expected, Missing(card, "currentRequiredMissing"));
        Assert.Equal(expected, Missing(card.GetProperty("report"), "requiredMissing"));
        Assert.NotNull(MarkText(card.GetProperty("report"), Customer));

        await SaveAsync(installer, id, Body(id, World.SiteA, [Last("Коридор"), Optional(), First("Щитовая")]));
        card = await ReadCardAsync(manager, id);
        Assert.Equal([(1, last, "materials")], Missing(card, "currentRequiredMissing"));
        Assert.Equal("не указаны обязательные материалы", MarkText(card.GetProperty("report"), Required));
        Assert.Equal(1, Count(await ReadQueueAsync(manager), Required));

        await SaveAsync(installer, id, Body(id, World.SiteA, [Last("Коридор", material.Id), Optional()]));
        queue = await ReadQueueAsync(manager, $"?mark={Customer}");
        Assert.Equal(0, Count(queue, Required));
        Assert.Equal(1, Count(queue, Customer));
        Assert.Null(MarkText(Assert.Single(queue.GetProperty("items").EnumerateArray()), Required));
        card = await ReadCardAsync(manager, id);
        Assert.Empty(Missing(card, "currentRequiredMissing"));
        Assert.Equal(expected, Missing(card.GetProperty("report"), "requiredMissing"));
        Assert.Contains("requiredMissing", Assert.Single(await period.Review.EntriesAsync(id),
            item => item.Kind == ReportJournalKinds.AutoSubmitted).Changes);

        var historyCount = (await period.Review.EntriesAsync(id)).Count;
        var (_, editor) = await World.Assignments.UserAsync("Редактор вида работ", CorePermissions.WorkTypesEdit);
        await SetPlaceRequirementAsync(editor, World.WorkType, required: true);
        Assert.Equal([(2, optional, "location")], Missing(await ReadCardAsync(manager, id), "currentRequiredMissing"));
        Assert.Equal(1, Count(await ReadQueueAsync(manager), Required));
        await SetPlaceRequirementAsync(editor, World.WorkType, required: false);
        Assert.Equal(0, Count(await ReadQueueAsync(manager), Required));
        Assert.Equal(historyCount, (await period.Review.EntriesAsync(id)).Count);
    }

    [Fact]
    public async Task Страницы_и_чтение_видов_следуют_охвату_и_отбору()
    {
        var both = await BothAsync();
        var (employee, installer) = await World.InstallerAsync("Монтажник А");
        var (_, second) = await World.InstallerAsync("Монтажник Б");
        var (_, foreign) = await World.InstallerAsync("Монтажник другой стройки");
        var (_, manager) = await World.ManagerAsync("Менеджер", World.SiteA);
        var (_, reader) = await World.Assignments.UserAsync("Руководитель", WorkModule.ReportRead);
        var (old, newer, together, outside) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        const string earlier = "2026-11-15";
        await CreateAsync(installer, Body(old, World.SiteA, [Line(Guid.NewGuid(), both)], earlier));
        await CreateAsync(installer, Body(newer, World.SiteA, [Line(Guid.NewGuid(), both), Line(Guid.NewGuid(), both)]));
        await CreateAsync(second, Body(together, World.SiteA, [Line(Guid.NewGuid(), both)]));
        await CreateAsync(foreign, Body(outside, World.SiteB, [Line(Guid.NewGuid(), World.WorkTypeNeedsPlace)]));
        await period.Review.SubmittedAsync(second, Guid.NewGuid(), World.SiteA, date: earlier);
        await period.Review.TickAsync(ReviewWorld.Up19);

        var expected = new[] { old }.Concat(new[] { newer, together }.Order()).ToArray();
        for (var offset = 0; offset < expected.Length; offset++)
        {
            var page = await ReadQueueAsync(manager, $"?mark={Required}&limit=1&offset={offset}");
            Assert.Equal(3, Count(page, Required));
            Assert.Equal(3, page.GetProperty("total").GetInt32());
            Assert.Equal(expected[offset], Assert.Single(Ids(page)));
        }
        Assert.Equal(newer, Assert.Single(Ids(await ReadQueueAsync(manager,
            $"?mark={Required}&constructionId={World.SiteA}&employeeId={employee}&from={ReportWorld.Day16}&to={ReportWorld.Day16}"))));
        Assert.Equal(0, Count(await ReadQueueAsync(manager, $"?constructionId={World.SiteB}"), Required));
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(CardPath(outside))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync(ReviewQueue.Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync(CardPath(newer))).StatusCode);
        Assert.Equal(4, Count(await ReadQueueAsync(reader), Required));
        var card = await ReadCardAsync(reader, outside);
        Assert.Empty(card.GetProperty("actions").EnumerateArray());
        Assert.Equal("не указано обязательное место работ", MarkText(card.GetProperty("report"), Required));

        await using var scope = host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var reads = sp.GetRequiredService<ReportReads>();
        var periods = sp.GetRequiredService<IModulePeriods>();
        var port = new TypesProbe(sp.GetRequiredService<IModuleWorkTypes>());
        var viewer = ReportViewer.Of(ModuleReach.OfSites([World.SiteA]));
        var filter = new ReviewQueueFilter(null, null, null, null, ReviewMarks.Of(Required));
        var read = await reads.QueueAsync(viewer, filter, null, 0, 1, port, periods, default);
        Assert.Equal([both], Assert.Single(port.Requests));
        Assert.Equal(3, read.Total);
        port.Unavailable = true;
        read = await reads.QueueAsync(viewer, filter, null, 0, 1, port, periods, default);
        Assert.Equal(0, read.Counts[Required]);
        Assert.Empty(read.Items);
    }

    [Fact]
    public async Task Возврат_повторная_подача_приёмка_и_закрытие()
    {
        var (_, installer) = await World.InstallerAsync("Монтажник");
        var (_, manager) = await World.ManagerAsync("Менеджер", World.SiteA);
        var (id, closedId, line, closedLine) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var route = $"{ReportPath}/{id}";
        await CreateAsync(installer, Body(id, World.SiteA, [Line(line, World.WorkTypeNeedsPlace)]));
        await CreateAsync(installer, Body(closedId, World.SiteA, [Line(closedLine, World.WorkTypeNeedsPlace)], WorkPeriodWorld.OctoberEnd));
        await period.Review.TickAsync(ReviewWorld.Up19);
        host.ReportTime = ReviewWorld.Up19.ToUniversalTime();
        var submitted = await installer.GetFromJsonAsync<JsonElement>(route);
        Assert.Equal(HttpStatusCode.BadRequest, (await ReviewWorld.AcceptAsync(manager, id, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ReviewWorld.AcceptAsync(manager, id, "0")).StatusCode);
        var returned = await ReviewWorld.ReturnAsync(manager, id, ReviewWorld.Version(submitted), "Нужно место");
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        await period.Review.TickAsync(ReviewWorld.Up19.AddHours(1));
        Assert.Equal([closedId], Ids(await ReadQueueAsync(manager, $"?mark={Required}")));
        var version = ReviewWorld.Version(await ReportWorld.JsonAsync(returned));
        var refused = await SubmissionTests.SubmitAsync(installer, route, version, ReviewWorld.Up19);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal([(1, line, "location")], Missing(await ReportWorld.JsonAsync(refused), "requiredMissing"));
        Assert.Equal("returned", (await installer.GetFromJsonAsync<JsonElement>(route)).GetProperty("status").GetString());
        var repaired = await SaveAsync(installer, id, Body(id, World.SiteA, [Line(line, World.WorkTypeNeedsPlace, "Коридор")]));
        var resubmitted = await SubmissionTests.SubmitAsync(installer, route, ReviewWorld.Version(repaired), ReviewWorld.Up19);
        Assert.Equal(HttpStatusCode.OK, resubmitted.StatusCode);
        Assert.Equal("manual", (await ReportWorld.JsonAsync(resubmitted)).GetProperty("submittedBy").GetString());
        Assert.Empty(Missing(await ReadCardAsync(manager, id), "currentRequiredMissing"));

        var closingReport = await ReadCardAsync(manager, closedId);
        var accepted = await ReviewWorld.AcceptAsync(manager, closedId, ReviewWorld.Version(closingReport.GetProperty("report")));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(0, Count(await ReadQueueAsync(manager), Required));
        var acceptedCard = await ReadCardAsync(manager, closedId);
        Assert.Empty(Missing(acceptedCard, "currentRequiredMissing"));
        Assert.Null(MarkText(acceptedCard.GetProperty("report"), Required));
        host.ClosureTime = ReviewWorld.Up19.ToUniversalTime();
        await WorkPeriodWorld.CloseAsync(await period.CloserAsync());
        var closed = await ReadCardAsync(manager, closedId);
        Assert.Empty(Missing(closed, "currentRequiredMissing"));
        Assert.Equal([(1, closedLine, "location")], Missing(closed.GetProperty("report"), "requiredMissing"));
        var edit = await ReportWorld.PutAsync(installer, $"{ReportPath}/{closedId}", ReviewWorld.Version(closed.GetProperty("report")),
            Body(closedId, World.SiteA, [Line(closedLine, World.WorkTypeNeedsPlace, "Щитовая")], WorkPeriodWorld.OctoberEnd));
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Contains("Период закрыт", await AssignmentWorld.ErrorAsync(edit));

        var correction = AssignmentWorld.Versioned(HttpMethod.Post, $"{ReportPath}/{closedId}/correction",
            ReviewWorld.Version(closed.GetProperty("report")), Body(closedId, World.SiteA,
                [Line(closedLine, World.WorkTypeNeedsPlace, "Щитовая")], WorkPeriodWorld.OctoberEnd, "Указано место работ"));
        Assert.Equal(HttpStatusCode.OK, (await manager.SendAsync(correction)).StatusCode);
        var atClosing = await manager.GetFromJsonAsync<JsonElement>(CardPath(closedId) + "?number=1");
        Assert.Empty(Missing(atClosing, "currentRequiredMissing"));
        Assert.Null(MarkText(atClosing.GetProperty("report"), Required));
        Assert.Empty(atClosing.GetProperty("actions").EnumerateArray());
    }

    [Fact]
    public async Task Досылка_к_принятому_сохраняет_снимок_без_активной_пометки()
    {
        var (_, installer) = await World.InstallerAsync("Монтажник");
        var (_, manager) = await World.ManagerAsync("Менеджер", World.SiteA);
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var body = Body(id, World.SiteA, [Line(line, World.WorkTypeNeedsPlace)]);
        await CreateAsync(installer, body);
        await period.Review.TickAsync(ReviewWorld.Up19);
        var submitted = (await ReadCardAsync(manager, id)).GetProperty("report");
        Assert.Equal(HttpStatusCode.OK, (await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(submitted))).StatusCode);
        host.ReportTime = ReviewWorld.Up19.ToUniversalTime();
        await SaveAsync(installer, id, Body(id, World.SiteA,
            [Line(line, World.WorkTypeNeedsPlace), Line(Guid.NewGuid(), World.WorkType)]));
        var queue = await ReadQueueAsync(manager, $"?mark={ReviewMarks.AcceptedChanged}");
        Assert.Equal(id, Assert.Single(Ids(queue)));
        Assert.Equal(0, Count(queue, Required));
        var card = await ReadCardAsync(manager, id);
        Assert.Empty(Missing(card, "currentRequiredMissing"));
        var report = card.GetProperty("report");
        Assert.Equal("manual", report.GetProperty("submittedBy").GetString());
        Assert.Equal([(1, line, "location")], Missing(report, "requiredMissing"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Закрытие_без_Work_снимает_пометку_после_включения_модуля(bool company)
    {
        using var installer = (await World.InstallerAsync("Монтажник")).Client;
        using var reader = (await World.Assignments.UserAsync("Руководитель", WorkModule.ReportRead)).Client;
        using var closer = await period.CloserAsync();
        var (closed, open, line) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var october = WorkPeriodWorld.OctoberEnd;
        await CreateAsync(installer, Body(closed, World.SiteA, [Line(line, World.WorkTypeNeedsPlace)], october));
        await CreateAsync(installer, Body(open, World.SiteB, [Line(Guid.NewGuid(), World.WorkTypeNeedsPlace)],
            company ? ReportWorld.Day16 : october));
        await period.Review.TickAsync(ReviewWorld.Up19);
        Assert.Equal(2, Count(await ReadQueueAsync(reader), Required));

        host.ClosureTime = ReviewWorld.Up19.ToUniversalTime();
        await using (var withoutWork = host.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Modules:Enabled", "id");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Modules:Enabled"] = "id" }));
        }))
        {
            using var closingClient = withoutWork.CreateClient();
            Assert.DoesNotContain(withoutWork.Services.GetRequiredService<ModuleRegistry>().Enabled, m => m.Code == WorkModule.ModuleCode);
            closingClient.DefaultRequestHeaders.Authorization = closer.DefaultRequestHeaders.Authorization;
            await WorkPeriodWorld.CloseAsync(closingClient, site: company ? null : World.SiteA);
        }

        await using var withWork = host.WithWebHostBuilder(_ => { });
        using var review = withWork.CreateClient();
        review.DefaultRequestHeaders.Authorization = reader.DefaultRequestHeaders.Authorization;
        var queue = await ReadQueueAsync(review);
        var row = Assert.Single(queue.GetProperty("items").EnumerateArray(), item => item.GetProperty("id").GetGuid() == closed);
        Assert.Null(MarkText(row, Required));
        Assert.Equal(1, Count(queue, Required));
        Assert.Equal([open], Ids(await ReadQueueAsync(review, $"?mark={Required}")));

        var card = await ReadCardAsync(review, closed);
        var report = card.GetProperty("report");
        Assert.True(report.GetProperty("periodClosed").GetBoolean());
        Assert.Equal("submitted", report.GetProperty("status").GetString());
        Assert.Equal([(1, line, "location")], Missing(report, "requiredMissing"));
        Assert.Null(MarkText(report, Required));
        Assert.Empty(Missing(card, "currentRequiredMissing"));
    }

    private async Task MarkCustomerFieldAsync()
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.ProjectModuleTypesAsync();
        var (_, admin) = await World.Assignments.UserAsync("Администратор типов", CorePermissions.TypesRead, CorePermissions.TypesEdit);
        var types = await admin.GetFromJsonAsync<JsonElement>("/api/document-types");
        var type = Assert.Single(types.EnumerateArray(), item => item.GetProperty("code").GetString() == WorkRecordTypes.LineDetailsCode);
        var path = $"/api/document-types/{type.GetProperty("id").GetGuid()}";
        var details = await admin.GetFromJsonAsync<JsonElement>(path);
        var schema = JsonNode.Parse(details.GetProperty("schema").GetRawText())!.AsObject();
        schema["fields"]!.AsArray().Add(new JsonObject { ["key"] = "act", ["type"] = "string", ["title"] = "Акт", ["forAcceptance"] = true });
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{path}/schema", new { schema = schema.ToJsonString() })).StatusCode);
    }

    private async Task<Guid> BothAsync()
    {
        var existing = (await seed.SendAsync(new GetCommonDataEntryQuery(World.WorkTypeNeedsPlace)))!;
        var data = JsonNode.Parse(existing.Data.RootElement.GetRawText())!.AsObject();
        data["Код"] = "01.04";
        data["Наименование"] = "Монтаж шкафа";
        data["ТребуетМатериалы"] = true;
        return (await seed.RecordAsync(existing.CompositeTypeId, "Монтаж шкафа", data.ToJsonString())).Id;
    }

    private async Task SetPlaceRequirementAsync(HttpClient editor, Guid id, bool required)
    {
        var current = (await seed.SendAsync(new GetCommonDataEntryQuery(id)))!;
        var data = JsonNode.Parse(current.Data.RootElement.GetRawText())!.AsObject();
        data["ТребуетМесто"] = required;
        var request = AssignmentWorld.Versioned(HttpMethod.Put, $"/api/work-types/{id}", current.Version,
            new { current.DisplayName, data = data.ToJsonString() });
        Assert.Equal(HttpStatusCode.OK, (await editor.SendAsync(request)).StatusCode);
    }

    private static async Task CreateAsync(HttpClient client, object body) =>
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(ReportPath, body)).StatusCode);

    private static async Task<JsonElement> SaveAsync(HttpClient client, Guid id, object body)
    {
        var route = $"{ReportPath}/{id}";
        var version = ReviewWorld.Version(await client.GetFromJsonAsync<JsonElement>(route));
        var response = await ReportWorld.PutAsync(client, route, version, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReportWorld.JsonAsync(response);
    }

    private object Line(Guid id, Guid workTypeId, string? location = null, Guid? nomenclatureId = null) => new
    {
        id, workTypeId, unitId = World.Unit, volume = 12, location,
        materials = nomenclatureId is { } material ? new[] { new { nomenclatureId = material } } : [],
    };

    private static object Body(Guid id, Guid constructionId, object[] lines, string date = ReportWorld.Day16, string? reason = null) =>
        new { id, constructionId, date, shift = "day", hours = 8, changeId = Guid.NewGuid(), lines, reason };

    private static string CardPath(Guid id) => $"/api/work/review/reports/{id}";
    private static Task<JsonElement> ReadCardAsync(HttpClient client, Guid id) => client.GetFromJsonAsync<JsonElement>(CardPath(id));
    private static Task<JsonElement> ReadQueueAsync(HttpClient client, string query = "") => client.GetFromJsonAsync<JsonElement>(ReviewQueue.Path + query);
    private static int Count(JsonElement queue, string code) => queue.GetProperty("counts").GetProperty(code).GetInt32();
    private static Guid[] Ids(JsonElement queue) => queue.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToArray();

    private static (int Line, Guid Id, string? Field)[] Missing(JsonElement record, string key) => record.GetProperty(key).EnumerateArray()
        .Select(item => (item.GetProperty("line").GetInt32(), item.GetProperty("lineId").GetGuid(), item.GetProperty("field").GetString())).ToArray();

    private static string? MarkText(JsonElement record, string code) => record.GetProperty("marks").EnumerateArray()
        .Where(mark => mark.GetProperty("code").GetString() == code).Select(mark => mark.GetProperty("text").GetString()).SingleOrDefault();

    private sealed class TypesProbe(IModuleWorkTypes inner) : IModuleWorkTypes
    {
        public Task<IReadOnlyList<ModuleWorkType>?> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        public List<Guid[]> Requests { get; } = [];
        public bool Unavailable { get; set; }

        public Task<IReadOnlyList<ModuleWorkType>?> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
        {
            Requests.Add([.. ids]);
            return Unavailable ? Task.FromResult<IReadOnlyList<ModuleWorkType>?>(null) : inner.GetAsync(ids, ct);
        }

        public Task<ModuleChoice<ModuleWorkType>?> SearchAsync(string? query, int limit, CancellationToken ct = default) =>
            inner.SearchAsync(query, limit, ct);

        public Task<IReadOnlyList<ModuleWorkVariant>?> GetVariantsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            inner.GetVariantsAsync(ids, ct);
    }
}
