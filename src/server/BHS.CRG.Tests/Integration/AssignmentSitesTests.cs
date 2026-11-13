using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Access;
using BHS.CRG.Modules.Work.Data;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Integration.AssignmentWorld;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Свои стройки (ТЗ WORK-24, WORK-8, CORE-24.3) и охват менеджера (ТЗ WORK-46, WORK-39, WORK-40; change
/// <c>work-11-assignments</c>). Часы порта хоста — 20:00 31.10.2026 по Москве (<see cref="AssignmentHost" />).
/// </summary>
[Collection(AssignmentHost.Collection)]
public class AssignmentSitesTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly AssignmentWorld world = new(host);
    private Guid _type;

    public async Task InitializeAsync()
    {
        await world.ResetAsync();
        _type = await world.DirectoryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Из 40 строек приходят ровно две назначенные; во Владивостоке в 20:00 по Москве «сегодня» — уже 01.11, и
    /// назначение, начавшееся 01.11, там уже действует.
    /// </summary>
    [Fact]
    public async Task Из_сорока_строек_приходят_две_назначенные_с_сегодня_по_поясу_стройки()
    {
        var installer = await world.EmployeeAsync(_type, "Лебедев Артём Ильич");
        var (userId, client) = await world.UserAsync("Лебедев А. И.", WorkModule.ReportOwn);
        await world.LinkAsync(installer, userId);
        for (var i = 1; i <= 38; i++) await world.SiteAsync($"Стройка {i:00}");
        var moscow = await world.SiteAsync("Склад на Каширке");
        var vladivostok = await world.SiteAsync("Порт Владивосток", "Asia/Vladivostok");
        await world.AssignedAsync(installer, moscow, AssignmentKind.Installer, "2026-10-20", "2026-11-10");
        await world.AssignedAsync(installer, vladivostok, AssignmentKind.Installer, "2026-11-01", "2026-11-30");

        var response = await client.GetAsync("/api/work/sites/mine");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await JsonAsync(response);
        var items = body.GetProperty("items").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetGuid());
        Assert.Equal(new[] { moscow, vladivostok }.Order(), items.Keys.Order());
        Assert.Equal("2026-11-01", items[vladivostok].GetProperty("today").GetString());
        Assert.Equal("2026-10-31", items[moscow].GetProperty("today").GetString());
        Assert.Equal(["installer"], items[moscow].GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));
        var boundary = body.GetProperty("boundary");
        Assert.Equal("ваши стройки (2)", boundary.GetProperty("text").GetString());
        Assert.Equal(2, boundary.GetProperty("count").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, boundary.GetProperty("reason").ValueKind);

        // На названную дату — по ней, а не по «сегодня»: 15.11 Москва уже кончилась.
        var later = await JsonAsync(await client.GetAsync("/api/work/sites/mine?date=2026-11-15"));
        Assert.Equal([vladivostok], later.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task Без_назначений_not_assigned_без_связи_not_linked()
    {
        var installer = await world.EmployeeAsync(_type, "Фомин Роман");
        var (userId, linked) = await world.UserAsync("Фомин Р.", WorkModule.ReportOwn);
        await world.LinkAsync(installer, userId);
        var (_, stranger) = await world.UserAsync("Без связи", WorkModule.ReportOwn);

        var none = await JsonAsync(await linked.GetAsync("/api/work/sites/mine"));
        Assert.Empty(none.GetProperty("items").EnumerateArray());
        Assert.Equal("not-assigned", none.GetProperty("boundary").GetProperty("reason").GetString());
        Assert.Equal("ваши стройки (0)", none.GetProperty("boundary").GetProperty("text").GetString());

        var unlinked = await JsonAsync(await stranger.GetAsync("/api/work/sites/mine"));
        Assert.Equal("not-linked", unlinked.GetProperty("boundary").GetProperty("reason").GetString());
    }

    /// <summary>
    /// Охват менеджера читается заново на каждом запросе: снятое назначение убирает стройку со следующего же;
    /// открытый конец действует на любую дату после начала; «на любых стройках» — все.
    /// </summary>
    [Fact]
    public async Task Охват_менеджера_из_действующих_назначений_и_снятие_действует_сразу()
    {
        var employee = await world.EmployeeAsync(_type, "Орлов Максим Сергеевич");
        var (userId, client) = await world.ManagerAsync(employee);
        var siteA = await world.SiteAsync("Стройка А");
        var siteB = await world.SiteAsync("Стройка Б");
        await world.AssignedAsync(employee, siteA, AssignmentKind.Manager, "2026-06-01", null);
        await world.AssignedAsync(employee, siteB, AssignmentKind.Manager, "2026-06-01", null);
        var reader = ModuleReader.Of(userId, "Орлов М. С.", [WorkModule.Assign, WorkModule.ModuleCode]);

        using (var scope = host.Services.CreateScope())
        {
            var sites = scope.ServiceProvider.GetRequiredService<ManagerSites>();
            var reach = await sites.ReachAsync(reader);
            Assert.Equal(ModuleReachKind.Sites, reach.Kind);
            Assert.Equal(new[] { siteA, siteB }.Order(), reach.Sites.Order());
            Assert.Equal(2, (await sites.OfEmployeeAsync(employee, Day("2099-01-01"))).Count);
            Assert.Empty(await sites.OfEmployeeAsync(employee, Day("2026-05-31")));

            var all = ModuleReader.Of(userId, "Орлов М. С.", [WorkModule.Assign, WorkModule.SitesAll, WorkModule.ModuleCode]);
            Assert.Equal(ModuleReachKind.All, (await sites.ReachAsync(all)).Kind);
            Assert.Empty((await sites.ReachAsync(ModuleReader.OfSystem("ночной пересчёт"))).Sites);
        }

        // Снимает другой менеджер стройки Б — на ней остаётся он сам, и стройка у Орлова уходит.
        var colleague = await world.EmployeeAsync(_type, "Соколова Вера Андреевна");
        var (_, other) = await world.ManagerAsync(colleague);
        await world.AssignedAsync(colleague, siteB, AssignmentKind.Manager, "2026-06-01", null);
        var mine = (await JsonAsync(await other.GetAsync($"/api/work/assignments?constructionId={siteB}")))
            .GetProperty("items").EnumerateArray().Single(i => i.GetProperty("employeeId").GetGuid() == employee);
        Assert.Equal(HttpStatusCode.NoContent, (await other.SendAsync(Versioned(HttpMethod.Delete,
            $"/api/work/assignments/{mine.GetProperty("id").GetGuid()}", mine.GetProperty("version").GetString()))).StatusCode);

        using (var scope = host.Services.CreateScope())
            Assert.Equal([siteA], (await scope.ServiceProvider.GetRequiredService<ManagerSites>().ReachAsync(reader)).Sites);
        var write = await client.PostAsJsonAsync("/api/work/assignments",
            Body(colleague, siteB, "installer", "2026-11-01", "2026-11-15"));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }
}
