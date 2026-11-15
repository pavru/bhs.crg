using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Чтение журнала отчёта под изоляцией и его ворота (change <c>work-14-report-journal</c>; ТЗ WORK-39, WORK-54, AUTH-8,
/// AUTH-9): чужой, чужой стройки и черновик — 404 тем же ответом, что несуществующий; пишущих адресов нет — 405.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class ReportJournalAccessTests(AssignmentHost host) : IAsyncLifetime
{
    private static readonly DateTimeOffset Submitted = new(2026, 11, 16, 21, 0, 0, TimeSpan.Zero);
    private readonly ReportWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Journal(Guid id) => $"{ReportEndpoints.Path}/{id}/journal";

    /// <summary>Монтажник А о журнале отчёта монтажника Б — 404 с тем же текстом, что о несуществующем.</summary>
    [Fact]
    public async Task Чужой_отчёт_404_как_несуществующий()
    {
        var (_, petrov) = await world.InstallerAsync("Петров Пётр Петрович");
        var (_, sidorov) = await world.InstallerAsync("Сидоров Семён Семёнович");
        var id = await CreateAsync(petrov, world.SiteA);
        await ReportJournalTests.SubmitAsync(host, id, Submitted);

        var foreign = await sidorov.GetAsync(Journal(id));
        var missing = await sidorov.GetAsync(Journal(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await AssignmentWorld.ErrorAsync(missing), await AssignmentWorld.ErrorAsync(foreign));
        Assert.Equal(HttpStatusCode.OK, (await petrov.GetAsync(Journal(id))).StatusCode);
    }

    /// <summary>Менеджер стройки А о журнале поданного отчёта стройки Б — 404; о поданном своей стройки — 200.</summary>
    [Fact]
    public async Task Менеджер_чужой_стройки_404()
    {
        var (_, installer) = await world.InstallerAsync("Петров Пётр Петрович");
        var (_, manager) = await world.ManagerAsync("Иванов Иван Иванович", world.SiteA);
        var onB = await CreateAsync(installer, world.SiteB);
        var onA = await CreateAsync(installer, world.SiteA);
        await ReportJournalTests.SubmitAsync(host, onB, Submitted);
        await ReportJournalTests.SubmitAsync(host, onA, Submitted);

        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(Journal(onB))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(Journal(onA))).StatusCode);
    }

    /// <summary>
    /// Черновик монтажника своей стройки — менеджеру 404 (содержимое — после подачи); черновик, который менеджер сам ввёл
    /// за сотрудника, — 200.
    /// </summary>
    [Fact]
    public async Task Черновик_у_менеджера_404_а_введённый_им_самим_200()
    {
        var (_, installer) = await world.InstallerAsync("Петров Пётр Петрович");
        var (_, manager) = await world.ManagerAsync("Иванов Иван Иванович", world.SiteA);
        var draft = await CreateAsync(installer, world.SiteA);
        var sidorov = await world.EmployeeAsync("Сидоров Семён Семёнович");
        var entered = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await manager.PostAsJsonAsync(EntryReportEndpoints.Path,
            world.Draft(entered, world.SiteA, employee: sidorov))).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync(Journal(draft))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(Journal(entered))).StatusCode);
    }

    /// <summary>
    /// Руководитель (<c>work.report.read</c>) — журнал поданного отчёта любой стройки, 200; черновика — 404. Имя автора
    /// — снимком: учётную запись автора удалили, журнал называет его по имени.
    /// </summary>
    [Fact]
    public async Task Руководитель_читает_поданный_отчёт_любой_стройки()
    {
        var installer = await world.EmployeeAsync("Петров Пётр Петрович");
        var (userId, client) = await world.Assignments.UserAsync("Петров Пётр Петрович", WorkModule.ReportOwn);
        await world.Assignments.LinkAsync(installer, userId);
        var (_, chief) = await world.Assignments.UserAsync("Руководитель", WorkModule.ReportRead);
        var submitted = await CreateAsync(client, world.SiteB);
        var draft = await CreateAsync(client, world.SiteA);
        await ReportJournalTests.SubmitAsync(host, submitted, Submitted);

        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.DeleteAsync((await users.FindByIdAsync(userId.ToString()))!)).Succeeded);
        }

        var response = await chief.GetAsync(Journal(submitted));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single((await ReportWorld.JsonAsync(response)).GetProperty("items").EnumerateArray());
        Assert.Equal("Петров Пётр Петрович", entry.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await chief.GetAsync(Journal(draft))).StatusCode);
    }

    /// <summary>
    /// Удалённый черновик журнал оставляет записью «удалён», и её читают те же, кто читал черновик: сотрудник отчёта и
    /// автор ввода — 200; другой монтажник, другой менеджер той же стройки и руководитель — 404.
    /// </summary>
    [Fact]
    public async Task Журнал_удалённого_черновика_читают_сотрудник_и_автор_ввода()
    {
        var (petrov, installer) = await world.InstallerAsync("Петров Пётр Петрович");
        var (_, sidorov) = await world.InstallerAsync("Сидоров Семён Семёнович");
        var (_, ivanov) = await world.ManagerAsync("Иванов Иван Иванович", world.SiteA);
        var (_, kuznetsov) = await world.ManagerAsync("Кузнецов Кузьма Кузьмич", world.SiteA);
        var (_, chief) = await world.Assignments.UserAsync("Руководитель", WorkModule.ReportRead);
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await ivanov.PostAsJsonAsync(EntryReportEndpoints.Path,
            world.Draft(id, world.SiteA, employee: petrov))).StatusCode);
        var version = (await ReportWorld.JsonAsync(await installer.GetAsync($"{ReportEndpoints.Path}/{id}")))
            .GetProperty("version").GetString();
        Assert.Equal(HttpStatusCode.NoContent,
            (await ReportWorld.DeleteAsync(installer, $"{ReportEndpoints.Path}/{id}", version)).StatusCode);

        foreach (var reader in new[] { installer, ivanov })
        {
            var response = await reader.GetAsync(Journal(id));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var kinds = (await ReportWorld.JsonAsync(response)).GetProperty("items").EnumerateArray()
                .Select(e => e.GetProperty("kind").GetString()).ToList();
            Assert.Equal(["created", "deleted"], kinds);
        }
        foreach (var stranger in new[] { sidorov, kuznetsov, chief })
            Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(Journal(id))).StatusCode);
    }

    /// <summary>
    /// Права складываются: менеджер стройки А со своими отчётами читает журнал своего отчёта стройки Б, которой у
    /// него нет в охвате менеджера.
    /// </summary>
    [Fact]
    public async Task Менеджер_со_своими_отчётами_читает_свой_журнал_вне_своих_строек()
    {
        var (_, manager) = await world.ManagerAsync("Иванов Иван Иванович", world.SiteA, WorkModule.ReportOwn);
        var onB = await CreateAsync(manager, world.SiteB);

        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync(Journal(onB))).StatusCode);
    }

    /// <summary>
    /// Свой сотрудник у читателя журнала расширяет чтение, а не запись: стройку вне охвата <c>Covers</c> не пускает —
    /// иначе такой охват, попав в путь записи, перенёс бы отчёт на чужую стройку мимо отказа «не ваша стройка».
    /// </summary>
    [Fact]
    public void Свой_сотрудник_у_широкого_охвата_не_открывает_запись_на_чужой_стройке()
    {
        var (site, other, me) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var viewer = ReportViewer.Of(ModuleReach.OfSites([site]), me);

        Assert.True(viewer.Covers(site, me));
        Assert.False(viewer.Covers(other, me));
    }

    /// <summary>Без прав модуля — отказ ворот, а не 404: адрес закрыт раньше, чем ищется отчёт.</summary>
    [Fact]
    public async Task Без_прав_модуля_отказ_ворот()
    {
        var (_, installer) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = await CreateAsync(installer, world.SiteA);
        var (_, stranger) = await world.Assignments.UserAsync("Посторонний");
        var (_, assigner) = await world.Assignments.UserAsync("Назначающий", WorkModule.Assign);

        Assert.Equal(HttpStatusCode.Forbidden, (await stranger.GetAsync(Journal(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await assigner.GetAsync(Journal(id))).StatusCode);
    }

    /// <summary>Пишущих адресов у журнала нет: <c>POST</c>, <c>PUT</c> и <c>DELETE</c> — 405.</summary>
    [Fact]
    public async Task Записать_журнал_адресом_нельзя()
    {
        var (_, installer) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = await CreateAsync(installer, world.SiteA);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var response = await installer.SendAsync(new HttpRequestMessage(method, Journal(id)) { Content = JsonContent.Create(new { }) });
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    private async Task<Guid> CreateAsync(HttpClient client, Guid site)
    {
        var id = Guid.NewGuid();
        var response = await client.PostAsJsonAsync(ReportEndpoints.Path, world.Draft(id, site));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return id;
    }
}
