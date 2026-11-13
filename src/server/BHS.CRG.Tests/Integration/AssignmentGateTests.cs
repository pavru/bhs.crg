using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Integration.AssignmentWorld;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Право по стройке у назначений (ТЗ WORK-21, WORK-46, AUTH-8, AUTH-9; change <c>work-11-assignments</c>): назначать
/// — только на свои стройки, <c>work.sites.all</c> — на любые; правка и снятие — по версии; признаки чтения
/// совпадают с ответом пишущего адреса.
///
/// <para>Сторож change — «назначение на чужую стройку — 403»: право, проверенное без стройки, пропустило бы
/// менеджера стройки А на стройку Б.</para>
///
/// <para>Отказ выключенного модуля (501) на <c>/api/work/assignments</c> проверяет <c>PlanOnlyHostTests</c>: хост
/// без <c>work</c>.</para>
/// </summary>
[Collection(AssignmentHost.Collection)]
public class AssignmentGateTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly AssignmentWorld world = new(host);
    private Guid _type;
    private Guid _siteA;
    private Guid _siteB;
    private Guid _manager;
    private Guid _installer;

    public async Task InitializeAsync()
    {
        await world.ResetAsync();
        _type = await world.DirectoryAsync();
        _siteA = await world.SiteAsync("Стройка А");
        _siteB = await world.SiteAsync("Стройка Б");
        _manager = await world.EmployeeAsync(_type, "Орлов Максим Сергеевич");
        _installer = await world.EmployeeAsync(_type, "Лебедев Артём Ильич");
        await world.AssignedAsync(_manager, _siteA, AssignmentKind.Manager, "2026-06-01", null);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Назначение_на_чужую_стройку_403_на_свою_201()
    {
        var (_, client) = await world.ManagerAsync(_manager);

        var foreign = await client.PostAsJsonAsync("/api/work/assignments",
            Body(_installer, _siteB, "installer", "2026-11-01", "2026-11-15"));
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Contains("не менеджер этой стройки", await ErrorAsync(foreign));
        Assert.Equal(0, await world.CountAsync(_siteB));

        var own = await client.PostAsJsonAsync("/api/work/assignments",
            Body(_installer, _siteA, "installer", "2026-11-01", "2026-11-15"));
        Assert.Equal(HttpStatusCode.Created, own.StatusCode);
        var created = await JsonAsync(own);
        Assert.Equal("installer", created.GetProperty("kind").GetString());
        Assert.Equal("2026-11-15", created.GetProperty("dateTo").GetString());
    }

    [Fact]
    public async Task Право_на_любые_стройки_открывает_чужую()
    {
        var (_, client) = await world.UserAsync("Администратор", WorkModule.Assign, WorkModule.SitesAll);

        var response = await client.PostAsJsonAsync("/api/work/assignments",
            Body(_installer, _siteB, "installer", "2026-11-01", "2026-11-15"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Учётная_запись_без_сотрудника_403_с_причиной()
    {
        var (_, client) = await world.UserAsync("Без сотрудника", WorkModule.Assign);

        var response = await client.PostAsJsonAsync("/api/work/assignments",
            Body(_installer, _siteA, "installer", "2026-11-01", "2026-11-15"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("не связана с сотрудником", await ErrorAsync(response));
        var read = await JsonAsync(await client.GetAsync($"/api/work/assignments?constructionId={_siteA}"));
        Assert.False(read.GetProperty("construction").GetProperty("canAssign").GetBoolean());
        Assert.Contains("не связана с сотрудником", read.GetProperty("construction").GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Монтажник_без_права_назначать_403()
    {
        var (userId, client) = await world.UserAsync("Монтажник", WorkModule.ReportOwn);
        await world.LinkAsync(_installer, userId);

        var write = await client.PostAsJsonAsync("/api/work/assignments",
            Body(_installer, _siteA, "installer", "2026-11-01", "2026-11-15"));
        var read = await client.GetAsync($"/api/work/assignments?constructionId={_siteA}");

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
    }

    /// <summary>
    /// Признак у стройки и у назначения — тот же ответ, что даёт пишущий адрес: своя стройка — можно, чужая —
    /// нельзя «вы не менеджер», единственный менеджер — снять нельзя «не останется менеджера» (D15 зонтика).
    /// </summary>
    [Fact]
    public async Task Признаки_чтения_совпадают_с_ответом_записи()
    {
        var (_, client) = await world.ManagerAsync(_manager);

        var own = await JsonAsync(await client.GetAsync($"/api/work/assignments?constructionId={_siteA}"));
        Assert.True(own.GetProperty("construction").GetProperty("canAssign").GetBoolean());
        Assert.Equal("2026-10-31", own.GetProperty("construction").GetProperty("today").GetString());
        var self = own.GetProperty("items").EnumerateArray().Single();
        Assert.True(self.GetProperty("active").GetBoolean());
        Assert.True(self.GetProperty("canChange").GetBoolean());
        Assert.False(self.GetProperty("canRemove").GetBoolean());
        Assert.Contains("не останется менеджера", self.GetProperty("removeReason").GetString());

        var removed = await client.SendAsync(Versioned(HttpMethod.Delete,
            $"/api/work/assignments/{self.GetProperty("id").GetGuid()}", self.GetProperty("version").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, removed.StatusCode);
        Assert.Contains("не останется менеджера", await ErrorAsync(removed));
        Assert.Equal(1, await world.CountAsync(_siteA));

        var foreign = await JsonAsync(await client.GetAsync($"/api/work/assignments?constructionId={_siteB}"));
        Assert.False(foreign.GetProperty("construction").GetProperty("canAssign").GetBoolean());
        Assert.Contains("не менеджер этой стройки", foreign.GetProperty("construction").GetProperty("reason").GetString());
        var write = await client.PostAsJsonAsync("/api/work/assignments",
            Body(_installer, _siteB, "installer", "2026-11-01", "2026-11-15"));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    /// <summary>Последнего менеджера снимает «на любых стройках»; сократить его до прошлого — тоже только оно.</summary>
    [Fact]
    public async Task Последнего_менеджера_снимает_только_право_на_любые_стройки()
    {
        var (_, manager) = await world.ManagerAsync(_manager);
        var (_, admin) = await world.UserAsync("Администратор", WorkModule.Assign, WorkModule.SitesAll);
        var self = (await JsonAsync(await manager.GetAsync($"/api/work/assignments?constructionId={_siteA}")))
            .GetProperty("items").EnumerateArray().Single();
        var url = $"/api/work/assignments/{self.GetProperty("id").GetGuid()}";
        var version = self.GetProperty("version").GetString();

        var shortened = await manager.SendAsync(Versioned(HttpMethod.Put, url, version,
            new { dateFrom = "2026-06-01", dateTo = "2026-10-01" }));
        Assert.Equal(HttpStatusCode.Conflict, shortened.StatusCode);
        Assert.Contains("не останется менеджера", await ErrorAsync(shortened));

        var adminView = (await JsonAsync(await admin.GetAsync($"/api/work/assignments?constructionId={_siteA}")))
            .GetProperty("items").EnumerateArray().Single();
        Assert.True(adminView.GetProperty("canRemove").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(Versioned(HttpMethod.Delete, url, version))).StatusCode);
    }

    [Fact]
    public async Task Правка_без_версии_400_по_устаревшей_409_смена_стройки_400()
    {
        var (_, client) = await world.ManagerAsync(_manager);
        var id = await world.AssignedAsync(_installer, _siteA, AssignmentKind.Installer, "2026-11-01", "2026-11-15");
        var url = $"/api/work/assignments/{id}";
        var version = (await JsonAsync(await client.GetAsync($"/api/work/assignments?constructionId={_siteA}")))
            .GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id)
            .GetProperty("version").GetString()!;
        var period = new { dateFrom = "2026-11-01", dateTo = "2026-11-20" };

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Versioned(HttpMethod.Put, url, null, period))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Versioned(HttpMethod.Delete, url, null))).StatusCode);

        var moved = await client.SendAsync(Versioned(HttpMethod.Put, url, version,
            new { constructionId = _siteB, dateFrom = "2026-11-01", dateTo = "2026-11-20" }));
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
        Assert.Contains("constructionId", await ErrorAsync(moved));

        var changed = await client.SendAsync(Versioned(HttpMethod.Put, url, version, period));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal("2026-11-20", (await JsonAsync(changed)).GetProperty("dateTo").GetString());

        var stale = await client.SendAsync(Versioned(HttpMethod.Put, url, version,
            new { dateFrom = "2026-11-01", dateTo = "2026-11-25" }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("тем временем изменили", await ErrorAsync(stale));
        using var scope = host.Services.CreateScope();
        Assert.Equal(Day("2026-11-20"),
            (await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Assignments.SingleAsync(a => a.Id == id)).DateTo);
    }

    /// <summary>Снятие — в журнал ядра: кто, кого, на какую стройку, какой вид и прежний период (ТЗ CORE-28).</summary>
    [Fact]
    public async Task Снятие_оставляет_в_журнале_прежний_период()
    {
        var (userId, client) = await world.ManagerAsync(_manager);
        var created = await JsonAsync(await client.PostAsJsonAsync("/api/work/assignments",
            Body(_installer, _siteA, "installer", "2026-11-01", "2026-11-15")));
        var id = created.GetProperty("id").GetGuid();

        var removed = await client.SendAsync(Versioned(HttpMethod.Delete, $"/api/work/assignments/{id}",
            created.GetProperty("version").GetString()));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        using var scope = host.Services.CreateScope();
        var journal = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Set<Domain.Activity.ActivityRecord>().AsNoTracking()
            .Where(r => r.TargetId == id.ToString()).OrderBy(r => r.OccurredAt).ToListAsync();
        Assert.Equal(["work.assignment.created", "work.assignment.removed"], journal.Select(r => r.Action));
        var removal = journal[1];
        Assert.Equal(userId, removal.ActorId);
        Assert.Equal("Лебедев Артём Ильич — Стройка А, монтажник", removal.TargetLabel);
        Assert.Equal("01.11.2026–15.11.2026", removal.Before);
    }

    /// <summary>Проверка базы — на запись мимо сущности: бессрочный монтажник отвергается самой базой.</summary>
    [Fact]
    public async Task База_отвергает_монтажника_без_конца_периода()
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());

        var refusal = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO work.assignments (id, employee_id, construction_id, kind, date_from, date_to, created_at, updated_at)
             VALUES ({Guid.NewGuid()}, {_installer}, {_siteA}, 'Installer', {Day("2026-11-01")}, NULL, now(), now())
             """));
        Assert.Equal("ck_assignments_installer_end", refusal.ConstraintName);
    }

    [Fact]
    public async Task Монтажник_без_конца_и_конец_раньше_начала_400()
    {
        var (_, client) = await world.ManagerAsync(_manager);

        var open = await client.PostAsJsonAsync("/api/work/assignments", Body(_installer, _siteA, "installer", "2026-11-01", null));
        Assert.Equal(HttpStatusCode.BadRequest, open.StatusCode);
        Assert.Contains("конец периода", await ErrorAsync(open));

        var reversed = await client.PostAsJsonAsync("/api/work/assignments",
            Body(_installer, _siteA, "installer", "2026-11-15", "2026-11-01"));
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        Assert.Equal(0, await world.CountAsync(_siteA) - 1);
    }

    [Fact]
    public async Task Менеджер_без_конца_периода_сохраняется()
    {
        var other = await world.EmployeeAsync(_type, "Соколова Вера Андреевна");
        var (otherUser, _) = await world.UserAsync("Соколова В. А.");
        await world.LinkAsync(other, otherUser);
        var (_, client) = await world.ManagerAsync(_manager);

        var response = await client.PostAsJsonAsync("/api/work/assignments", Body(other, _siteA, "manager", "2026-11-01", null));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await JsonAsync(response)).GetProperty("dateTo").ValueKind);
    }
}
