using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Documents;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Work;

internal sealed class CrewWorld(AssignmentHost host)
{
    public const string Path = "/api/work/crew-groups";
    public ReviewWorld Review { get; } = new(host);
    public ReportWorld Reports => Review.Reports;
    public Guid Site => Reports.SiteA;
    public Task SeedAsync() => Review.SeedAsync();

    public Task AssignAsync(Guid employee, Guid? site = null, string from = "2026-11-01", string to = "2026-11-30") =>
        Reports.Assignments.AssignedAsync(employee, site ?? Site, AssignmentKind.Installer, from, to);

    public object Body(Guid report, Guid line, decimal volume, string? role = "lead", Guid[]? participants = null,
        string shift = "day", Guid? site = null, Guid? unit = null) =>
        new
        {
            id = report, constructionId = site ?? Site, date = ReportWorld.Day16, shift, hours = 8m, changeId = Guid.NewGuid(),
            lines = new[]
            {
                new { id = line, workTypeId = Reports.WorkType, volume, unitId = unit ?? Reports.Unit, isCrew = true,
                    crewRole = role, participants, location = "оси 3–4" },
            },
        };

    public static async Task<JsonElement> CreateAsync(HttpClient client, object body) =>
        await JsonAsync(await client.PostAsJsonAsync(ReportEndpoints.Path, body), HttpStatusCode.Created);

    public static async Task<JsonElement> SubmitAsync(HttpClient client, Guid id, JsonElement draft) =>
        await JsonAsync(await SubmissionTests.SubmitAsync(client, $"{ReportEndpoints.Path}/{id}", ReviewWorld.Version(draft),
            ReviewWorld.Evening16));

    public async Task<JsonElement> SubmittedAsync(HttpClient client, Guid id, Guid line, decimal volume,
        string role = "lead", Guid[]? participants = null, string shift = "day", Guid? site = null, Guid? unit = null) =>
        await SubmitAsync(client, id, await CreateAsync(client, Body(id, line, volume, role, participants, shift, site, unit)));

    public async Task<JsonElement[]> GroupsAsync(HttpClient client, Guid? site = null)
    {
        var body = await JsonAsync(await client.GetAsync($"{Path}?constructionId={site ?? Site}&from={ReportWorld.Day16}&to={ReportWorld.Day16}"));
        return body.GetProperty("items").EnumerateArray().ToArray();
    }

    public static Task<HttpResponseMessage> ApproveAsync(HttpClient client, Guid id, string? version, decimal total = 120m) =>
        client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Post, $"{Path}/{id}/approve", version, new { total }));

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(response.StatusCode == expected, $"{response.StatusCode} вместо {expected}: {await response.Content.ReadAsStringAsync()}");
        return await ReportWorld.JsonAsync(response);
    }

    public async Task<Guid> AnotherUnitAsync()
    {
        using var scope = host.Services.CreateScope();
        var type = await scope.ServiceProvider.GetRequiredService<IModuleTypes>().FindAsync(CoreRecordTypes.UnitCode);
        return (await new WorkTypeSeed(host).RecordAsync(type!.Value, "шт")).Id;
    }
}
