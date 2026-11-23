using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Ввод отчёта за сотрудника (<c>/api/work/entry/reports</c>; change <c>work-13-report-model</c>, ТЗ WORK-31, WORK-2,
/// AUTH-8): сотрудник из тела, автор ввода из учётной записи, охват — стройки менеджера.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class EntryReportEndpointsTests(AssignmentHost host) : IAsyncLifetime
{
    private const string Url = EntryReportEndpoints.Path;
    private readonly ReportWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Ввод_за_сотрудника_видит_только_пересечение_назначений_и_охвата()
    {
        var (_, manager) = await world.ManagerAsync("Менеджер стройки А", world.SiteA);
        var both = await world.EmployeeAsync("Назначен на обе стройки");
        var outside = await world.EmployeeAsync("Назначен вне охвата");
        var ended = await world.EmployeeAsync("Закончено до даты");
        await world.Assignments.AssignedAsync(both, world.SiteA, AssignmentKind.Installer, "2026-10-01", "2026-12-31");
        await world.Assignments.AssignedAsync(both, world.SiteB, AssignmentKind.Installer, "2026-10-01", "2026-12-31");
        await world.Assignments.AssignedAsync(outside, world.SiteB, AssignmentKind.Installer, "2026-10-01", "2026-12-31");
        await world.Assignments.AssignedAsync(ended, world.SiteA, AssignmentKind.Installer, "2026-10-01", "2026-11-15");
        var list = await manager.GetFromJsonAsync<System.Text.Json.JsonElement>($"{Url}/candidates?date={ReportWorld.Day16}");
        var ids = list.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("employeeId").GetGuid()).ToArray();
        Assert.Contains(both, ids);
        Assert.DoesNotContain(outside, ids);
        Assert.DoesNotContain(ended, ids);
        var siteList = await manager.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"{Url}/candidates/{both}/sites?date={ReportWorld.Day16}");
        var site = Assert.Single(siteList.GetProperty("items").EnumerateArray());
        Assert.Equal(world.SiteA, site.GetProperty("id").GetGuid());
        Assert.NotNull(site.GetProperty("today").GetString());
        Assert.NotNull(site.GetProperty("suggested").GetProperty("label").GetString());
        var boundary = siteList.GetProperty("boundary");
        Assert.Equal(1, boundary.GetProperty("count").GetInt32());
        var hidden = await manager.GetFromJsonAsync<System.Text.Json.JsonElement>($"{Url}/candidates/{outside}/sites");
        Assert.Empty(hidden.GetProperty("items").EnumerateArray());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, hidden.GetProperty("employeeName").ValueKind);
    }

    [Fact]
    public async Task Справочники_ввода_работают_без_общих_прав_и_не_выдают_чужую_стройку()
    {
        var (employee, installer) = await world.InstallerAsync("Назначенный монтажник");
        await world.Assignments.AssignedAsync(employee, world.SiteA, AssignmentKind.Installer, "2026-10-01", "2026-12-31");
        var (_, manager) = await world.ManagerAsync("Менеджер стройки А", world.SiteA);
        string Catalog(Guid site, string kind) => $"/api/work/entry/sites/{site}/catalog/{kind}?date={ReportWorld.Day16}";
        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync("/api/work-types")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await installer.GetAsync($"{Url}/candidates")).StatusCode);
        var types = await installer.GetFromJsonAsync<System.Text.Json.JsonElement>(Catalog(world.SiteA, "work-types"));
        Assert.Contains(types.GetProperty("items").EnumerateArray(),
            t => t.GetProperty("id").GetGuid() == world.WorkTypeNeedsPlace && t.GetProperty("requiresLocation").GetBoolean());
        var units = await installer.GetFromJsonAsync<System.Text.Json.JsonElement>(Catalog(world.SiteA, "units"));
        Assert.Contains(units.GetProperty("items").EnumerateArray(), u => u.GetProperty("id").GetGuid() == world.Unit);
        foreach (var client in new[] { installer, manager })
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Catalog(world.SiteA, "sections"))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Catalog(world.SiteA, "materials"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Catalog(world.SiteB, "sections"))).StatusCode);
        }
    }

    [Fact]
    public async Task Архивный_материал_виден_в_сохранённом_отчёте_и_отсутствует_в_выборе()
    {
        var seed = new WorkTypeSeed(host);
        var type = await seed.TypeAsync(TestDirectories.NomenclatureCode, "Номенклатура");
        using (var scope = host.Services.CreateScope())
            await TestDirectories.AssignRolesAsync(scope.ServiceProvider);
        var old = await seed.RecordAsync(type.Id, "Кабель прежний");
        var current = await seed.RecordAsync(type.Id, "Кабель действующий");
        var (employee, installer) = await world.InstallerAsync("Назначенный монтажник");
        await world.Assignments.AssignedAsync(employee, world.SiteA, AssignmentKind.Installer, "2026-10-01", "2026-12-31");

        var reportId = Guid.NewGuid();
        var created = await installer.PostAsJsonAsync(ReportEndpoints.Path, new
        {
            id = reportId, constructionId = world.SiteA, date = ReportWorld.Day16, shift = "day",
            lines = new[]
            {
                new { id = Guid.NewGuid(), workTypeId = world.WorkType, unitId = world.Unit, volume = 1,
                    materials = new[] { new { nomenclatureId = old.Id } } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await seed.ArchiveAsync(old.Id);

        var path = $"/api/work/entry/sites/{world.SiteA}/catalog/materials";
        var choices = await installer.GetFromJsonAsync<JsonElement>($"{path}?date={ReportWorld.Day16}&q=Кабель");
        Assert.Equal(current.Id, Assert.Single(choices.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Empty(choices.GetProperty("selected").EnumerateArray());

        var withSelected = await installer.GetFromJsonAsync<JsonElement>($"{path}?reportId={reportId}&q=Кабель");
        Assert.Equal(current.Id, Assert.Single(withSelected.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        var saved = Assert.Single(withSelected.GetProperty("selected").EnumerateArray());
        Assert.Equal(old.Id, saved.GetProperty("id").GetGuid());
        Assert.True(saved.GetProperty("archived").GetBoolean());
    }

    /// <summary>
    /// Менеджер стройки А вводит черновик Петрова на стройке А — отчёт Петрова, <c>enteredBy</c> — менеджер, и Петров
    /// видит его в своём курсоре.
    /// </summary>
    [Fact]
    public async Task Менеджер_вводит_за_сотрудника_без_смартфона()
    {
        var (_, manager) = await world.ManagerAsync("Сидорова Анна Сергеевна", world.SiteA);
        var (petrov, petrovClient) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();

        var response = await manager.PostAsJsonAsync(Url, world.Draft(id, world.SiteA, employee: petrov));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReportWorld.JsonAsync(response);
        Assert.Equal(petrov, body.GetProperty("employeeId").GetGuid());
        Assert.Equal("Сидорова Анна Сергеевна", body.GetProperty("enteredBy").GetProperty("name").GetString());

        var own = await ReportWorld.JsonAsync(await petrovClient.GetAsync($"{ReportEndpoints.Path}/{id}"));
        Assert.Equal("Сидорова Анна Сергеевна", own.GetProperty("enteredBy").GetProperty("name").GetString());
        var read = await ReportWorld.JsonAsync(await manager.GetAsync($"{Url}/{id}"));
        Assert.Equal(id, read.GetProperty("id").GetGuid());
    }

    /// <summary>Отчёт чужой стройки — 404 и на чтении, и на правке, тот же, что у несуществующего.</summary>
    [Fact]
    public async Task Отчёт_чужой_стройки_неотличим_от_несуществующего()
    {
        var (_, manager) = await world.ManagerAsync("Сидорова Анна Сергеевна", world.SiteA);
        var (_, petrov) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var created = await ReportWorld.JsonAsync(await petrov.PostAsJsonAsync(ReportEndpoints.Path, world.Draft(id, world.SiteB)));

        var get = await manager.GetAsync($"{Url}/{id}");
        var missing = await manager.GetAsync($"{Url}/{Guid.NewGuid()}");
        var put = await ReportWorld.PutAsync(manager, $"{Url}/{id}", created.GetProperty("version").GetString(),
            world.Draft(id, world.SiteB, 1m, changeId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(await AssignmentWorld.ErrorAsync(missing), await AssignmentWorld.ErrorAsync(get));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(8m, (await world.ReportAsync(id))!.Hours);
    }

    /// <summary>Заведение на чужой стройке — 404 «не найдена среди ваших строек», отчёт не заведён.</summary>
    [Fact]
    public async Task Заведение_на_чужой_стройке_отказ()
    {
        var (_, manager) = await world.ManagerAsync("Сидорова Анна Сергеевна", world.SiteA);
        var petrov = await world.EmployeeAsync("Петров Пётр Петрович");

        var response = await manager.PostAsJsonAsync(Url, world.Draft(Guid.NewGuid(), world.SiteB, employee: petrov));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ReportDesk.SiteNotYours, await AssignmentWorld.ErrorAsync(response));
        Assert.Equal(0, await world.ReportCountAsync());
    }

    /// <summary>Перенос отчёта правкой на чужую стройку — тот же 404: охват проверяется и у новой стройки.</summary>
    [Fact]
    public async Task Перенос_на_чужую_стройку_отказ()
    {
        var (_, manager) = await world.ManagerAsync("Сидорова Анна Сергеевна", world.SiteA);
        var petrov = await world.EmployeeAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var created = await ReportWorld.JsonAsync(await manager.PostAsJsonAsync(Url, world.Draft(id, world.SiteA, employee: petrov)));

        var response = await ReportWorld.PutAsync(manager, $"{Url}/{id}", created.GetProperty("version").GetString(),
            world.Draft(id, world.SiteB, employee: petrov, changeId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(world.SiteA, (await world.ReportAsync(id))!.ConstructionId);
    }

    /// <summary>Правка без <c>If-Match</c> — 400; по версии, которую сдвинула правка монтажника, — 409.</summary>
    [Fact]
    public async Task Правка_без_версии_и_по_устаревшей()
    {
        var (_, manager) = await world.ManagerAsync("Сидорова Анна Сергеевна", world.SiteA);
        var (petrov, petrovClient) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var created = await ReportWorld.JsonAsync(await manager.PostAsJsonAsync(Url, world.Draft(id, world.SiteA, employee: petrov)));
        var version = created.GetProperty("version").GetString();

        var unversioned = await ReportWorld.PutAsync(manager, $"{Url}/{id}", null,
            world.Draft(id, world.SiteA, 6m, employee: petrov, changeId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.BadRequest, unversioned.StatusCode);

        var byInstaller = await ReportWorld.PutAsync(petrovClient, $"{ReportEndpoints.Path}/{id}", version,
            world.Draft(id, world.SiteA, 7m, changeId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, byInstaller.StatusCode);

        var stale = await ReportWorld.PutAsync(manager, $"{Url}/{id}", version,
            world.Draft(id, world.SiteA, 6m, employee: petrov, changeId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ReportDesk.Stale, await AssignmentWorld.ErrorAsync(stale));
        Assert.Equal(7m, (await world.ReportAsync(id))!.Hours);
    }

    /// <summary>«На любых стройках» вводит и там, где менеджером не назначен (WORK-46).</summary>
    [Fact]
    public async Task На_любых_стройках_вводит_везде()
    {
        var (_, manager) = await world.ManagerAsync("Сидорова Анна Сергеевна", world.SiteA, WorkModule.SitesAll);
        var petrov = await world.EmployeeAsync("Петров Пётр Петрович");

        var response = await manager.PostAsJsonAsync(Url, world.Draft(Guid.NewGuid(), world.SiteB, employee: petrov));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>Монтажник с одними своими отчётами — 403 на адресах ввода (AUTH-8).</summary>
    [Fact]
    public async Task Без_права_приёмки_403()
    {
        var (petrov, client) = await world.InstallerAsync("Петров Пётр Петрович");

        var response = await client.PostAsJsonAsync(Url, world.Draft(Guid.NewGuid(), world.SiteA, employee: petrov));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>Сотрудник не назван — 400: за кого вводится отчёт, решает тело.</summary>
    [Fact]
    public async Task Сотрудник_обязателен()
    {
        var (_, manager) = await world.ManagerAsync("Сидорова Анна Сергеевна", world.SiteA);

        var response = await manager.PostAsJsonAsync(Url, world.Draft(Guid.NewGuid(), world.SiteA));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("employeeId", await AssignmentWorld.ErrorAsync(response));
    }
}
