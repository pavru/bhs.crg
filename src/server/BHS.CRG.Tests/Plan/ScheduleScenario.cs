using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Plan;
using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Tests.Integration;
using BHS.CRG.Tests.Support;
using static BHS.CRG.Tests.Plan.OfferDerivativeScenario;

namespace BHS.CRG.Tests.Plan;

internal sealed record ScheduleScenario(PlanOnlyHost Host, EstimateSeed.Site Site, Guid Estimate, HttpClient Editor)
{
    internal const string Root = "/api/plan/schedules";

    internal static async Task<ScheduleScenario> CreateAsync(PlanOnlyHost host)
    {
        var site = await EstimateSeed.SiteAsync(host);
        var estimate = await EstimateSeed.EstimateAsync(host, site.Construction);
        var editor = await GrantedSignIn.WithPermissionsAsync(host, PlanModule.ScheduleRead, PlanModule.ScheduleEdit,
            PlanModule.EstimateRead, PlanModule.EstimateEdit);
        return new(host, site, estimate, editor);
    }

    internal async Task<Guid> RevisionAsync(params EstimateRowDraft[] rows) =>
        (await EstimateSeed.RevisionAsync(Host, Estimate, rows)).Id;

    internal object Creation(Guid? revision, string name = "Электромонтаж") =>
        new { constructionId = Site.Construction, revisionId = revision, creationKey = Guid.NewGuid(), name };

    internal Task<JsonElement> BuildAsync(Guid? revision = null, string name = "Электромонтаж") =>
        ReadAsync(Editor.PostAsJsonAsync(Root, Creation(revision, name)), HttpStatusCode.Created);

    internal async Task<JsonElement> ManualAsync(JsonElement schedule, string? start = null, string? end = null)
    {
        var classifier = await EstimateSeed.ClassifierAsync(Host);
        return await ReadAsync(Editor.PostAsJsonAsync($"{ScheduleUrl(schedule)}/works",
            new { workTypeId = classifier.WorkType, work = new { volume = 1m, start, end } }));
    }

    internal Task<HttpResponseMessage> LinkAsync(
        JsonElement schedule, JsonElement from, JsonElement to, string kind = "fs", int lag = 0) =>
        Editor.PostAsJsonAsync($"{ScheduleUrl(schedule)}/links", Link(from, to, kind, lag));

    internal static object Link(JsonElement from, JsonElement to, string kind = "fs", int lag = 0) =>
        new { predecessorId = Id(from), successorId = Id(to), kind, lag };

    internal static Guid Id(JsonElement result) => result.GetProperty("id").GetGuid();
    internal static string ScheduleUrl(JsonElement schedule) => $"{Root}/{Id(schedule)}";
    internal static string WorkUrl(JsonElement schedule, JsonElement work) => $"{ScheduleUrl(schedule)}/works/{Id(work)}";
    internal static JsonElement[] Works(JsonElement schedule) => [.. schedule.GetProperty("works").EnumerateArray()];
    internal static int Skipped(JsonElement schedule) => schedule.GetProperty("skipped").GetProperty("unmapped").GetInt32();
    internal static Guid? GuidOrNull(JsonElement value, string name) =>
        value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : value.GetProperty(name).GetGuid();
}
