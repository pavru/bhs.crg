using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Work.Endpoints;

namespace BHS.CRG.Tests.Integration;

internal sealed class WorkPeriodWorld(AssignmentHost host)
{
    internal const string OctoberEnd = "2026-10-31";
    internal const string Company = "company";
    internal const string PeriodPath = "/api/periods";
    internal const string Reports = ReportEndpoints.Path;

    internal ReviewWorld Review { get; } = new(host);
    internal ReportWorld World => Review.Reports;

    internal async Task<HttpClient> CloserAsync() =>
        (await World.Assignments.UserAsync("Закрывающий период", CorePermissions.PeriodClose)).Client;

    internal static async Task<JsonElement> PreviewAsync(HttpClient closer, string through = OctoberEnd,
        Guid? site = null, string from = "2026-10-01", string seen = "none")
    {
        var response = await SendAsync(closer, through, site, from, seen, stamp: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReportWorld.JsonAsync(response);
    }

    internal static Task<HttpResponseMessage> SendAsync(HttpClient closer, string through, Guid? site,
        string from, string seen, string? stamp) =>
        closer.PostAsJsonAsync(PeriodPath + (stamp is null ? "/close/preview" : "/close"),
            new { contour = site is null ? Company : "construction", constructionId = site, from, through, ifMatch = seen, report = stamp });

    internal static async Task<Guid> CloseAsync(HttpClient closer, string through = OctoberEnd, Guid? site = null,
        string from = "2026-10-01", string seen = "none")
    {
        var preview = await PreviewAsync(closer, through, site, from, seen);
        Assert.True(preview.GetProperty("canClose").GetBoolean());
        var response = await SendAsync(closer, through, site, from, seen, Stamp(preview));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReportWorld.JsonAsync(response)).GetProperty("id").GetGuid();
    }

    internal static async Task ReopenCompanyAsync(HttpClient closer, string through = OctoberEnd)
    {
        var response = await closer.PostAsJsonAsync(PeriodPath + "/reopen",
            new { contour = Company, ifMatch = through, reason = "Уточнили данные" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    internal async Task<JsonElement> AcceptedAsync(HttpClient installer, HttpClient manager, Guid id, Guid site,
        string date = OctoberEnd, Guid? lineId = null)
    {
        var submitted = await Review.SubmittedAsync(installer, id, site, date, lineId);
        var response = await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(submitted));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReportWorld.JsonAsync(response);
    }

    internal static JsonElement Section(JsonElement preview) =>
        Assert.Single(preview.GetProperty("sections").EnumerateArray(), s => s.GetProperty("module").GetString() == "work");

    internal static string Stamp(JsonElement preview) => preview.GetProperty("stamp").GetString()!;
}
