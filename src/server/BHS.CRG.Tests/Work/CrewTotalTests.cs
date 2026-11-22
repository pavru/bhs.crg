using System.Net;
using System.Text.Json;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Tests.Integration;

namespace BHS.CRG.Tests.Work;

[Collection(AssignmentHost.Collection)]
public class CrewTotalTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly CrewWorld crew = new(host);

    public Task InitializeAsync() => crew.SeedAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Approval_records_one_total_and_journals_every_report_then_a_changed_line_makes_it_stale()
    {
        var (_, lead) = await crew.Reports.InstallerAsync("Бригадир");
        var (_, member) = await crew.Reports.InstallerAsync("Участник");
        var (_, other) = await crew.Reports.InstallerAsync("Третий");
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var changedLine = Guid.NewGuid();
        await crew.SubmittedAsync(lead, ids[0], Guid.NewGuid(), 120);
        await crew.SubmittedAsync(member, ids[1], Guid.NewGuid(), 120, "member");
        var report = await crew.SubmittedAsync(other, ids[2], changedLine, 100, "member");
        var pending = Assert.Single(await crew.GroupsAsync(manager));
        var id = pending.GetProperty("id").GetGuid();
        Assert.Equal("pending", pending.GetProperty("state").GetString());
        Assert.True(pending.GetProperty("mismatch").GetBoolean());
        Assert.Equal("approve", pending.GetProperty("actions")[0].GetString());
        Assert.Equal(new[] { 100m, 120m, 120m }, pending.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("volume").GetDecimal()).Order());
        Assert.DoesNotContain("hours", pending.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, await MarkCountAsync(manager));

        var approved = await CrewWorld.JsonAsync(await CrewWorld.ApproveAsync(manager, id, ReviewWorld.Version(pending)));
        Assert.Equal("approved", approved.GetProperty("state").GetString());
        Assert.Equal(120m, approved.GetProperty("total").GetDecimal());
        Assert.Equal(crew.Reports.Unit, approved.GetProperty("unitId").GetGuid());
        Assert.Equal("Менеджер", approved.GetProperty("approvedByName").GetString());
        Assert.NotEqual(Guid.Empty, approved.GetProperty("approvedBy").GetGuid());
        Assert.NotEqual(default, approved.GetProperty("approvedAt").GetDateTimeOffset());
        Assert.Empty(approved.GetProperty("actions").EnumerateArray());
        Assert.Equal(0, await MarkCountAsync(manager));
        var journalIds = new HashSet<Guid>();
        foreach (var reportId in ids)
        {
            var acted = Assert.Single(await crew.Review.EntriesAsync(reportId), e => e.Kind == ReportJournalKinds.CrewApproved);
            Assert.Equal("итог бригады: 120 м", acted.Comment);
            Assert.Equal("Менеджер", acted.AuthorName);
            journalIds.Add(acted.ChangeId);
        }
        Assert.Single(journalIds);

        await CrewWorld.JsonAsync(await ReportWorld.PutAsync(other, $"{ReportEndpoints.Path}/{ids[2]}", ReviewWorld.Version(report),
            crew.Body(ids[2], changedLine, 110, role: null)));
        var stale = Assert.Single(await crew.GroupsAsync(manager));
        Assert.Equal("stale", stale.GetProperty("state").GetString());
        Assert.Equal(120m, stale.GetProperty("total").GetDecimal());
        Assert.Equal("approve", stale.GetProperty("actions")[0].GetString());
        Assert.Equal(3, await MarkCountAsync(manager));
        var obsolete = await CrewWorld.ApproveAsync(manager, id, ReviewWorld.Version(approved));
        Assert.Equal(HttpStatusCode.Conflict, obsolete.StatusCode);
    }

    [Fact]
    public async Task Groups_separate_shifts_and_a_late_autosubmission_invalidates_the_total()
    {
        var (_, lead) = await crew.Reports.InstallerAsync("Бригадир");
        var (memberId, member) = await crew.Reports.InstallerAsync("Участник");
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        await crew.AssignAsync(memberId);
        var draftId = Guid.NewGuid();
        await CrewWorld.CreateAsync(member, crew.Body(draftId, Guid.NewGuid(), 100, "member"));
        await crew.SubmittedAsync(lead, Guid.NewGuid(), Guid.NewGuid(), 120, participants: [memberId]);
        await crew.SubmittedAsync(lead, Guid.NewGuid(), Guid.NewGuid(), 200, shift: "night");

        var groups = await crew.GroupsAsync(manager);
        Assert.Equal(2, groups.Length);
        var day = Assert.Single(groups, g => g.GetProperty("shift").GetString() == "day");
        Assert.Single(day.GetProperty("lines").EnumerateArray());
        Assert.Equal(1, day.GetProperty("unsubmitted").GetInt32());
        await CrewWorld.JsonAsync(await CrewWorld.ApproveAsync(manager, day.GetProperty("id").GetGuid(), ReviewWorld.Version(day)));
        await crew.Review.TickAsync(ReviewWorld.Up19);
        var late = Assert.Single(await crew.GroupsAsync(manager), g => g.GetProperty("shift").GetString() == "day");
        Assert.Equal("stale", late.GetProperty("state").GetString());
        Assert.Equal(2, late.GetProperty("lines").GetArrayLength());
        Assert.Equal(0, late.GetProperty("unsubmitted").GetInt32());
    }

    [Fact]
    public async Task Concurrent_submissions_share_one_group_and_mixed_units_refuse_approval()
    {
        var (_, lead) = await crew.Reports.InstallerAsync("Бригадир");
        var (_, member) = await crew.Reports.InstallerAsync("Участник");
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var otherUnit = await crew.AnotherUnitAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var leadDraft = await CrewWorld.CreateAsync(lead, crew.Body(first, Guid.NewGuid(), 120));
        var memberDraft = await CrewWorld.CreateAsync(member, crew.Body(second, Guid.NewGuid(), 100, "member", unit: otherUnit));
        await Task.WhenAll(CrewWorld.SubmitAsync(lead, first, leadDraft), CrewWorld.SubmitAsync(member, second, memberDraft));
        var group = Assert.Single(await crew.GroupsAsync(manager));
        Assert.Equal(2, group.GetProperty("lines").GetArrayLength());
        var refusal = await CrewWorld.ApproveAsync(manager, group.GetProperty("id").GetGuid(), ReviewWorld.Version(group));
        Assert.Equal(HttpStatusCode.BadRequest, refusal.StatusCode);
        var error = await AssignmentWorld.ErrorAsync(refusal);
        Assert.Contains("м, шт", error);
        Assert.Equal(JsonValueKind.Null, Assert.Single(await crew.GroupsAsync(manager)).GetProperty("total").ValueKind);
    }

    private async Task<int> MarkCountAsync(HttpClient manager)
    {
        var queue = await CrewWorld.JsonAsync(await manager.GetAsync(
            $"/api/work/review/queue?constructionId={crew.Site}&mark=crew-mismatch"));
        var items = queue.GetProperty("items").EnumerateArray().ToArray();
        Assert.All(items, row => Assert.Contains(row.GetProperty("marks").EnumerateArray(),
            mark => mark.GetProperty("code").GetString() == "crew-mismatch"));
        Assert.Equal(items.Length, queue.GetProperty("counts").GetProperty("crew-mismatch").GetInt32());
        return items.Length;
    }
}
