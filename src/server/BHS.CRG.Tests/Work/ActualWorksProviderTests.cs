using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Modules;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Tests.Integration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Work;

[Collection(AssignmentHost.Collection)]
public class ActualWorksProviderTests(AssignmentHost host) : IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 11, 16);
    private static readonly DateOnly October = new(2026, 10, 31);
    private readonly CrewWorld crew = new(host);
    private readonly WorkPeriodWorld period = new(host);

    public Task InitializeAsync() => crew.SeedAsync();

    public Task DisposeAsync()
    {
        host.ClosureTime = null;
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Ordinary_fact_uses_report_day_and_only_the_current_accepted_lines()
    {
        var (_, installer) = await crew.Reports.InstallerAsync("Ночная смена");
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site);
        var id = Guid.NewGuid();
        var line = Guid.NewGuid();
        var draft = await CrewWorld.CreateAsync(installer, crew.Reports.Draft(id, crew.Site,
            date: WorkPeriodWorld.OctoberEnd, shift: "night", lineId: line));
        var delivered = new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.FromHours(3));
        var submitted = await SubmitAsync(installer, id, draft, delivered);
        var accepted = await AcceptAsync(manager, id, submitted);
        host.ClosureTime = AssignmentHost.Now.AddDays(5);
        await WorkPeriodWorld.CloseAsync(await period.CloserAsync());
        var before = Assert.Single((await FactAsync(new(crew.Site, From: October, To: October))).Items);
        Assert.Equal((October, "night", 120m), (before.Date, before.Shift, before.Volume));
        Assert.Empty((await FactAsync(new(crew.Site, From: October.AddDays(1)))).Items);

        var correction = await ReportCorrectionTests.PostAsync(manager, id, ReviewWorld.Version(accepted), new
        {
            reason = "Исправлено по акту", constructionId = crew.Site, date = October, shift = "night", hours = 8, lines = new[]
            {
                new { id = line, workTypeId = crew.Reports.WorkType, unitId = crew.Reports.Unit, volume = 90 },
            },
        });
        await CrewWorld.JsonAsync(correction);
        var corrected = Assert.Single((await FactAsync(new(crew.Site))).Items);
        Assert.Equal(before with { Volume = 90m }, corrected);
        var closed = await installer.GetFromJsonAsync<JsonElement>($"/api/work/reports/{id}?view=at-closing");
        Assert.Equal(120m, closed.GetProperty("lines")[0].GetProperty("volume").GetDecimal());
    }

    [Fact]
    public async Task One_approved_total_is_published_only_after_accepting_every_participating_report()
    {
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var (first, a) = await CrewAsync(120);
        var (second, b) = await CrewAsync(120);
        var (third, c) = await CrewAsync(100);
        var query = new ActualWorksQuery(crew.Site, From: Day, To: Day);
        var pending = await FactAsync(query);
        Assert.Equal(3, pending.Diagnostics.AwaitingAcceptance);
        AssertExclusion(pending, CrewFactExclusion.NotApproved);
        var approved = await ApproveAsync(manager);
        Assert.Equal("approved", approved.GetProperty("state").GetString());
        await AcceptAsync(manager, first, a);
        await AcceptAsync(manager, second, b);
        var waiting = await FactAsync(query);
        Assert.Empty(waiting.Items);
        Assert.Equal(1, waiting.Diagnostics.AwaitingAcceptance);
        AssertExclusion(waiting, CrewFactExclusion.AwaitingAcceptance);
        var group = Assert.Single(await crew.GroupsAsync(manager));
        AssertPublication(group, false, "awaiting-acceptance", 2, 3, 0);

        await AcceptAsync(manager, third, c);
        var published = await FactAsync(query);
        var item = Assert.Single(published.Items);
        Assert.Equal(120m, item.Volume);
        Assert.Null(item.SectionId);
        Assert.Equal(0, published.Diagnostics.ExcludedCrewGroups);
        group = Assert.Single(await crew.GroupsAsync(manager));
        Assert.Equal(ReviewWorld.Version(approved), ReviewWorld.Version(group));
        AssertPublication(group, true, null, 3, 3, 0);
    }

    [Fact]
    public async Task A_concurrent_acceptance_cannot_mix_the_fact_with_counts_from_another_revision()
    {
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var (id, submitted) = await CrewAsync(120);
        await ApproveAsync(manager);
        var race = new AcceptAfterRead(
            sql => sql.Contains("count(*)", StringComparison.OrdinalIgnoreCase) && sql.Contains("FROM work.reports"),
            async () => await AcceptAsync(manager, id, submitted));
        await using var probed = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<WorkDbContext>(options => options.AddInterceptors(race))));
        using var scope = probed.Services.CreateScope();
        race.Armed = true;
        var reader = ModuleReader.Of(Guid.NewGuid(), "Читатель", [WorkModule.FactsRead]);
        var query = new ActualWorksQuery(crew.Site);
        var answer = await Connected(scope.ServiceProvider).ReadAsync(reader, query);
        var snapshot = Assert.IsType<ContractAnswer<ActualWorksAnswer>.Shown>(answer).Value;
        Assert.True(race.Changed);
        Assert.Equal(1, snapshot.Diagnostics.AwaitingAcceptance);
        AssertExclusion(snapshot, CrewFactExclusion.AwaitingAcceptance);
        Assert.Empty(snapshot.Items);

        var current = await FactAsync(query);
        Assert.Equal(0, current.Diagnostics.AwaitingAcceptance);
        Assert.Equal(0, current.Diagnostics.ExcludedCrewGroups);
        Assert.Equal(120m, Assert.Single(current.Items).Volume);
    }

    [Fact]
    public async Task Approval_response_includes_an_acceptance_committed_after_the_first_read()
    {
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var (id, submitted) = await CrewAsync(120);
        var pending = Assert.Single(await crew.GroupsAsync(manager));
        var groupId = pending.GetProperty("id").GetGuid();
        var race = new AcceptAfterRead(
            sql => sql.Contains("work.crew_groups") && sql.Contains("work.report_lines"),
            async () => await AcceptAsync(manager, id, submitted));
        await using var probed = host.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<WorkDbContext>(options => options.AddInterceptors(race))));
        using var approvalClient = probed.CreateClient();
        approvalClient.DefaultRequestHeaders.Authorization = manager.DefaultRequestHeaders.Authorization;
        race.Armed = true;

        var approved = await CrewWorld.JsonAsync(await CrewWorld.ApproveAsync(approvalClient, groupId, ReviewWorld.Version(pending)));
        Assert.True(race.Changed);
        AssertPublication(approved, true, null, 1, 1, 0);
        AssertPublication(Assert.Single(await crew.GroupsAsync(manager)), true, null, 1, 1, 0);
        Assert.Equal(120m, Assert.Single((await FactAsync(new(crew.Site))).Items).Volume);
    }

    [Fact]
    public async Task Submission_by_a_named_employee_is_shared_by_groups_in_the_same_shift_only()
    {
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var (_, lead) = await crew.Reports.InstallerAsync("Бригадир");
        var (member, memberClient) = await crew.Reports.InstallerAsync("Участник");
        await crew.AssignAsync(member);
        var day = Guid.NewGuid();
        var draft = await CrewWorld.CreateAsync(lead, new
        {
            id = day, constructionId = crew.Site, date = ReportWorld.Day16, shift = "day", hours = 8m,
            lines = new[] { crew.Reports.WorkType, crew.Reports.WorkTypeNeedsPlace }.Select(workTypeId => new
            {
                id = Guid.NewGuid(), workTypeId, unitId = crew.Reports.Unit, volume = 120, location = "оси 3–4",
                isCrew = true, crewRole = "lead", participants = new[] { member },
            }).ToArray(),
        });
        await CrewWorld.SubmitAsync(lead, day, draft);
        await crew.SubmittedAsync(lead, Guid.NewGuid(), Guid.NewGuid(), 120, participants: [member], shift: "night");
        var query = new ActualWorksQuery(crew.Site);
        Assert.Equal(3, (await FactAsync(query)).Diagnostics.Unsubmitted);

        await crew.Review.SubmittedAsync(memberClient, Guid.NewGuid(), crew.Site);
        var groups = await crew.GroupsAsync(manager);
        Assert.Equal(3, groups.Length);
        foreach (var group in groups)
            Assert.Equal(group.GetProperty("shift").GetString() == "day" ? 0 : 1, group.GetProperty("unsubmitted").GetInt32());
        Assert.Equal(1, (await FactAsync(query)).Diagnostics.Unsubmitted);
    }

    [Fact]
    public async Task Closed_crew_correction_preserves_history_and_requires_new_approval_to_publish()
    {
        var (_, installer) = await crew.Reports.InstallerAsync("Монтажник");
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var (report, line) = (Guid.NewGuid(), Guid.NewGuid());
        var body = crew.Body(report, line, 120, date: WorkPeriodWorld.OctoberEnd);
        var draft = await CrewWorld.CreateAsync(installer, body);
        var accepted = await AcceptAsync(manager, report, await SubmitAsync(installer, report, draft, AssignmentHost.Now));
        var approved = await ApproveAsync(manager, date: WorkPeriodWorld.OctoberEnd);
        host.ClosureTime = AssignmentHost.Now.AddDays(5);
        await WorkPeriodWorld.CloseAsync(await period.CloserAsync());
        var query = new ActualWorksQuery(crew.Site, From: October, To: October);
        Assert.Equal(120m, Assert.Single((await FactAsync(query)).Items).Volume);

        await CrewWorld.JsonAsync(await ReportCorrectionTests.PostAsync(manager, report, ReviewWorld.Version(accepted),
            crew.Body(report, line, 90, date: WorkPeriodWorld.OctoberEnd, reason: "Исправлено по акту")));
        var stale = await FactAsync(query);
        Assert.Empty(stale.Items);
        AssertExclusion(stale, CrewFactExclusion.Stale);
        var group = await CrewWorld.JsonAsync(await manager.GetAsync($"{CrewWorld.Path}/{approved.GetProperty("id").GetGuid()}"));
        Assert.Equal(120m, group.GetProperty("total").GetDecimal());
        var before = await installer.GetFromJsonAsync<JsonElement>($"/api/work/reports/{report}?view=at-closing");
        Assert.Equal(120m, before.GetProperty("lines")[0].GetProperty("volume").GetDecimal());
        await ApproveAsync(manager, 90, date: WorkPeriodWorld.OctoberEnd);
        Assert.Equal(90m, Assert.Single((await FactAsync(query)).Items).Volume);
        var journal = await crew.Review.EntriesAsync(report);
        Assert.Contains(journal, e => e.Kind == ReportJournalKinds.CrewApproved && e.Comment == "итог бригады: 120 м");
        Assert.Contains(journal, e => e.Kind == ReportJournalKinds.CrewApproved && e.Comment == "итог бригады: 90 м");
    }

    [Fact]
    public async Task Unsubmitted_is_only_a_warning_and_late_submission_and_return_invalidate_approval()
    {
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var (member, late) = await crew.Reports.InstallerAsync("Участник");
        await crew.AssignAsync(member);
        var lateId = Guid.NewGuid();
        var draft = await CrewWorld.CreateAsync(late, crew.Body(lateId, Guid.NewGuid(), 100, "member"));
        var (first, a) = await CrewAsync(120, [member]);
        var (second, b) = await CrewAsync(120, [member]);
        await AcceptAsync(manager, first, a);
        await AcceptAsync(manager, second, b);
        await ApproveAsync(manager);

        var query = new ActualWorksQuery(crew.Site);
        var fact = await FactAsync(query);
        Assert.Equal(120m, Assert.Single(fact.Items).Volume);
        Assert.Equal(0, fact.Diagnostics.ExcludedCrewGroups);
        Assert.Equal(1, fact.Diagnostics.Unsubmitted);
        AssertPublication(Assert.Single(await crew.GroupsAsync(manager)), true, null, 2, 2, 1);

        var submitted = await CrewWorld.SubmitAsync(late, lateId, draft);
        var stale = await FactAsync(query);
        Assert.Empty(stale.Items);
        AssertExclusion(stale, CrewFactExclusion.Stale);
        Assert.Equal(0, stale.Diagnostics.Unsubmitted);
        var approvedAgain = await ApproveAsync(manager);
        AssertPublication(approvedAgain, false, "awaiting-acceptance", 2, 3, 0);
        var returned = await CrewWorld.JsonAsync(await ReviewWorld.ReturnAsync(manager, lateId, ReviewWorld.Version(submitted),
            "Уточнить место"));
        var afterReturn = await FactAsync(query);
        AssertExclusion(afterReturn, CrewFactExclusion.Stale);
        Assert.Equal(1, afterReturn.Diagnostics.Unsubmitted);
        var resubmitted = await CrewWorld.SubmitAsync(late, lateId, returned);
        AssertExclusion(await FactAsync(query), CrewFactExclusion.Stale);
        await AcceptAsync(manager, lateId, resubmitted);
        await ApproveAsync(manager);
        Assert.Equal(120m, Assert.Single((await FactAsync(query)).Items).Volume);
    }

    [Fact]
    public async Task Filters_do_not_hide_multiple_keys_or_diagnostic_scope()
    {
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var firstSection = await SectionAsync("Корпус А");
        var secondSection = await SectionAsync("Корпус Б");
        var (first, a) = await CrewAsync(120, section: firstSection);
        var (second, b) = await CrewAsync(100, section: secondSection);
        await AcceptAsync(manager, first, a);
        await AcceptAsync(manager, second, b);
        var group = await ApproveAsync(manager);
        Assert.Equal(120m, group.GetProperty("total").GetDecimal());
        AssertPublication(group, false, "multiple-keys", 2, 2, 0);
        var keys = group.GetProperty("publication").GetProperty("keys").EnumerateArray().ToArray();
        Assert.Equal(2, keys.Length);
        var selected = Assert.Single(keys, key => key.GetProperty("sectionId").GetGuid() == firstSection);

        var full = await FactAsync(new(crew.Site, From: Day, To: Day));
        var narrow = await FactAsync(new(crew.Site, firstSection, [selected.GetProperty("planItemId").GetGuid()], Day, Day));
        Assert.Empty(full.Items);
        Assert.Empty(narrow.Items);
        AssertExclusion(narrow, CrewFactExclusion.MultipleKeys);
        Assert.Equal(full.Diagnostics.Scope, narrow.Diagnostics.Scope);
        Assert.Equal(full.Diagnostics.CrewExclusions, narrow.Diagnostics.CrewExclusions);
        Assert.Equal(0, narrow.Diagnostics.WithoutPlanItem);
    }

    [Fact]
    public async Task Invalid_approved_unit_is_excluded_and_an_empty_saved_group_adds_no_exclusion()
    {
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var (id, submitted) = await CrewAsync(120);
        await AcceptAsync(manager, id, submitted);
        var group = await ApproveAsync(manager);
        var unit = await crew.AnotherUnitAsync();
        var groupId = group.GetProperty("id").GetGuid();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE work.crew_groups SET unit_id = {unit} WHERE id = {groupId}");
        var answer = await FactAsync(new(crew.Site));
        AssertExclusion(answer, CrewFactExclusion.InvalidKey);
        Assert.Empty(answer.Items);
        Assert.Equal(0, answer.Diagnostics.WithoutPlanItem);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE work.report_lines SET is_crew = false, crew_role = NULL WHERE report_id = {id}");
        var empty = await FactAsync(new(crew.Site));
        Assert.Equal(0, empty.Diagnostics.ExcludedCrewGroups);
        Assert.Empty(empty.Diagnostics.CrewExclusions);
        AssertPublication(Assert.Single(await crew.GroupsAsync(manager)), false, "empty", 0, 0, 0);
    }

    [Fact]
    public async Task Defective_reference_counts_a_line_and_preserves_unscheduled_fact_and_other_reports()
    {
        var (_, installer) = await crew.Reports.InstallerAsync("Обычные работы");
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var (_, waiting) = await crew.Reports.InstallerAsync("Поданный");
        var (_, returned) = await crew.Reports.InstallerAsync("Возвращённый");
        var (_, draft) = await crew.Reports.InstallerAsync("Черновик");
        var id = Guid.NewGuid();
        var brokenLine = Guid.NewGuid();
        var created = await CrewWorld.CreateAsync(installer, new
        {
            id, constructionId = crew.Site, date = ReportWorld.Day16, hours = 8,
            lines = new[] { NormalLine(Guid.NewGuid(), 20), NormalLine(brokenLine, 90) },
        });
        await AcceptAsync(manager, id, await CrewWorld.SubmitAsync(installer, id, created));
        var waitingId = Guid.NewGuid();
        var waitingDraft = await CrewWorld.CreateAsync(waiting, new
        {
            id = waitingId, constructionId = crew.Site, date = ReportWorld.Day16, hours = 8,
            lines = new[] { NormalLine(Guid.NewGuid(), 100), NormalLine(Guid.NewGuid(), 200) },
        });
        await CrewWorld.SubmitAsync(waiting, waitingId, waitingDraft);
        var returnedId = Guid.NewGuid();
        var toReturn = await crew.Review.SubmittedAsync(returned, returnedId, crew.Site);
        await CrewWorld.JsonAsync(await ReviewWorld.ReturnAsync(manager, returnedId, ReviewWorld.Version(toReturn), "Уточнить"));
        await CrewWorld.CreateAsync(draft, crew.Reports.Draft(Guid.NewGuid(), crew.Site));
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
            // Защитный контроль испорченной ссылки. Штатные адреса всегда находят или создают позицию.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE work.report_lines SET work_plan_item_id = {Guid.NewGuid()} WHERE id = {brokenLine}");
        }

        var fact = await FactAsync(new(crew.Site));
        Assert.Equal(20m, Assert.Single(fact.Items).Volume);
        Assert.Equal(1, fact.Diagnostics.WithoutPlanItem);
        Assert.Equal(1, fact.Diagnostics.AwaitingAcceptance);
        Assert.Equal(0, fact.Diagnostics.ExcludedCrewGroups);
    }

    [Fact]
    public async Task Real_work_only_provider_refuses_hidden_and_system_reads_and_keeps_report_access_isolated()
    {
        var catalog = host.Services.GetRequiredService<ModuleContractCatalog>().Find(typeof(IActualWorks));
        Assert.NotNull(catalog);
        Assert.False(catalog.Many);
        Assert.Equal(WorkModule.ModuleCode, Assert.Single(catalog.Registrations).Module);
        using var scope = host.Services.CreateScope();
        var contract = Connected(scope.ServiceProvider);
        Assert.Equal([WorkModule.FactsRead], contract.AnswerPermissions);
        var query = new ActualWorksQuery(crew.Site);
        var denied = await contract.ReadAsync(ModuleReader.Of(Guid.NewGuid(), "Без права", [WorkModule.ReportRead]), query);
        Assert.Equal(WorkModule.FactsRead, Assert.IsType<ContractAnswer<ActualWorksAnswer>.Hidden>(denied).Permission);
        var system = await contract.ReadAsync(ModuleReader.OfSystem("ночная сверка"), query);
        Assert.Equal("ночная сверка", Assert.IsType<ContractAnswer<ActualWorksAnswer>.SystemRefused>(system).Reason);

        var (managerEmployee, manager) = await crew.Reports.ManagerAsync("Менеджер Б", crew.Reports.SiteB, WorkModule.FactsRead);
        var (_, localManager) = await crew.Reports.ManagerAsync("Менеджер А", crew.Site);
        var (_, installer) = await crew.Reports.InstallerAsync("Монтажник");
        var report = Guid.NewGuid();
        await AcceptAsync(localManager, report, await crew.Review.SubmittedAsync(installer, report, crew.Site));
        var accountId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().EmployeeAccounts
            .Where(link => link.EmployeeId == managerEmployee).Select(link => link.UserId).SingleAsync();
        Assert.NotNull(accountId);
        var reader = await scope.ServiceProvider.GetRequiredService<IModuleReaders>().ForJobAsync(
            new ModuleJobRun(Guid.NewGuid(), Guid.NewGuid(), accountId.Value, null, (_, _, _) => Task.CompletedTask));
        var otherSiteFact = Assert.IsType<ContractAnswer<ActualWorksAnswer>.Shown>(await contract.ReadAsync(reader, query));
        Assert.Equal(120m, Assert.Single(otherSiteFact.Value.Items).Volume);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/work/review/reports/{report}")).StatusCode);
        var (_, factsOnly) = await crew.Reports.Assignments.UserAsync("Только факт", WorkModule.FactsRead);
        Assert.Equal(HttpStatusCode.Forbidden, (await factsOnly.GetAsync(CrewWorld.Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factsOnly.GetAsync($"/api/work/review/reports/{report}")).StatusCode);
    }

    private object NormalLine(Guid id, decimal volume) => new
        { id, volume, workTypeId = crew.Reports.WorkType, unitId = crew.Reports.Unit };

    private async Task<Guid> SectionAsync(string name)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var section = Section.Create(crew.Site, name);
        db.Sections.Add(section);
        await db.SaveChangesAsync();
        return section.Id;
    }

    private async Task<(Guid Id, JsonElement Report)> CrewAsync(decimal volume, Guid[]? participants = null, Guid? section = null)
    {
        var id = Guid.NewGuid();
        var (_, installer) = await crew.Reports.InstallerAsync("Бригада");
        var draft = await CrewWorld.CreateAsync(installer,
            crew.Body(id, Guid.NewGuid(), volume, participants: participants, section: section));
        return (id, await CrewWorld.SubmitAsync(installer, id, draft));
    }

    private async Task<JsonElement> ApproveAsync(HttpClient manager, decimal total = 120, string date = ReportWorld.Day16)
    {
        var page = await CrewWorld.JsonAsync(await manager.GetAsync($"{CrewWorld.Path}?constructionId={crew.Site}&from={date}&to={date}"));
        var group = Assert.Single(page.GetProperty("items").EnumerateArray());
        return await CrewWorld.JsonAsync(await CrewWorld.ApproveAsync(manager,
            group.GetProperty("id").GetGuid(), ReviewWorld.Version(group), total));
    }

    private static async Task<JsonElement> SubmitAsync(HttpClient installer, Guid id, JsonElement draft, DateTimeOffset at) =>
        await CrewWorld.JsonAsync(await SubmissionTests.SubmitAsync(installer, $"/api/work/reports/{id}",
            ReviewWorld.Version(draft), at));

    private static async Task<JsonElement> AcceptAsync(HttpClient manager, Guid id, JsonElement report) =>
        await CrewWorld.JsonAsync(await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(report)));

    private static void AssertExclusion(ActualWorksAnswer answer, CrewFactExclusion expected)
    {
        Assert.Equal(1, answer.Diagnostics.ExcludedCrewGroups);
        Assert.Equal(new CrewFactExclusionCount(expected, 1), Assert.Single(answer.Diagnostics.CrewExclusions));
    }

    private static void AssertPublication(JsonElement group, bool published, string? reason, int accepted, int participating,
        int unsubmitted)
    {
        var view = group.GetProperty("publication");
        Assert.Equal(published, view.GetProperty("published").GetBoolean());
        Assert.Equal(reason, view.GetProperty("reason").GetString());
        Assert.Equal(accepted, view.GetProperty("accepted").GetInt32());
        Assert.Equal(participating, view.GetProperty("participating").GetInt32());
        Assert.Equal(unsubmitted, group.GetProperty("unsubmitted").GetInt32());
    }

    private async Task<ActualWorksAnswer> FactAsync(ActualWorksQuery query)
    {
        using var scope = host.Services.CreateScope();
        var contract = Connected(scope.ServiceProvider);
        var reader = ModuleReader.Of(Guid.NewGuid(), "Читатель факта", [WorkModule.FactsRead]);
        return Assert.IsType<ContractAnswer<ActualWorksAnswer>.Shown>(await contract.ReadAsync(reader, query)).Value;
    }

    private static IActualWorks Connected(IServiceProvider services) =>
        Assert.IsType<ModuleContractAnswer<IActualWorks>.Connected>(services.GetRequiredService<IModuleContracts>()
            .Get<IActualWorks>()).Contract;

    private sealed class AcceptAfterRead(Func<string, bool> isRead, Func<Task> accept) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public bool Changed { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken ct = default)
        {
            if (Armed && !Changed && isRead(command.CommandText))
            {
                Changed = true;
                await accept();
            }
            return result;
        }
    }
}
