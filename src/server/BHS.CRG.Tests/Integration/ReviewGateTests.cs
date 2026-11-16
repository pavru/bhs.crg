using System.Net;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Ворота и версия приёмки (change <c>work-16-review</c>; ТЗ WORK-39, WORK-46, AUTH-8, AUTH-9): <c>If-Match</c> у
/// приёмки, возврата и правки менеджером, право <c>work.report.review</c>, стройка — охватом (чужая — 404), право «на
/// любых стройках». Отказ выключенного модуля — <see cref="PlanOnlyHostTests" />. Сотрудники — вымышленные.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class ReviewGateTests(AssignmentHost host) : IAsyncLifetime
{
    private const string Url = ReportEndpoints.Path;
    private readonly ReviewWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Без версии — 400 у приёмки, возврата и правки менеджером; статус прежний.</summary>
    [Fact]
    public async Task Без_версии_отказ()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        await world.SubmittedAsync(petrov, id, world.SiteA, lineId: line);

        var accept = await ReviewWorld.AcceptAsync(manager, id, null);
        var back = await ReviewWorld.ReturnAsync(manager, id, null, "Объём");
        var edit = await ReportWorld.PutAsync(manager, $"{EntryReportEndpoints.Path}/{id}", null,
            world.Reports.Draft(id, world.SiteA, lineId: line, volume: 100m, changeId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, accept.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, back.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
        Assert.Equal(ReportStatus.Submitted, (await world.Reports.ReportAsync(id))!.Status);
    }

    /// <summary>
    /// Монтажник поправил отчёт после подачи: приёмка по просмотренной версии — 409 «отчёт тем временем изменили», и
    /// принятой остаётся только просмотренная версия; то же у возврата и правки менеджером.
    /// </summary>
    [Fact]
    public async Task Устаревшая_версия_отказ()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var seen = ReviewWorld.Version(await world.SubmittedAsync(petrov, id, world.SiteA, lineId: line));
        var put = await ReportWorld.PutAsync(petrov, $"{Url}/{id}", seen,
            world.Reports.Draft(id, world.SiteA, lineId: line, volume: 150m, changeId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var accept = await ReviewWorld.AcceptAsync(manager, id, seen);
        var back = await ReviewWorld.ReturnAsync(manager, id, seen, "Объём");
        var edit = await ReportWorld.PutAsync(manager, $"{EntryReportEndpoints.Path}/{id}", seen,
            world.Reports.Draft(id, world.SiteA, lineId: line, volume: 100m, changeId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Conflict, accept.StatusCode);
        Assert.Equal(ReportDesk.Stale, await AssignmentWorld.ErrorAsync(accept));
        Assert.Equal(HttpStatusCode.Conflict, back.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal(ReportStatus.Submitted, (await world.Reports.ReportAsync(id))!.Status);
    }

    /// <summary>
    /// Монтажник с одним <c>work.report.own</c> — 403 на свой отчёт; руководитель с <c>work.report.read</c> — 403, хотя
    /// журнал отчёта ему виден.
    /// </summary>
    [Fact]
    public async Task Монтажник_и_руководитель_403()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, head) = await world.Reports.Assignments.UserAsync("Смирнова Светлана Сергеевна", WorkModule.ReportRead);
        var id = Guid.NewGuid();
        var version = ReviewWorld.Version(await world.SubmittedAsync(petrov, id, world.SiteA));
        Assert.Equal(HttpStatusCode.OK, (await head.GetAsync($"{Url}/{id}/journal")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await ReviewWorld.AcceptAsync(petrov, id, version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ReviewWorld.ReturnAsync(petrov, id, version, "Сам")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ReviewWorld.AcceptAsync(head, id, version)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ReviewWorld.ReturnAsync(head, id, version, "Объём")).StatusCode);
        Assert.Equal(ReportStatus.Submitted, (await world.Reports.ReportAsync(id))!.Status);
    }

    /// <summary>
    /// Менеджер стройки Б принимает отчёт стройки А — 404, как на несуществующий; снятое назначение закрывает и свою
    /// стройку.
    /// </summary>
    [Fact]
    public async Task Менеджер_чужой_стройки_404()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, alien) = await world.Reports.ManagerAsync("Кузнецов Кирилл Константинович", world.SiteB);
        // Назначение «менеджер» на стройку А кончилось 15.10 — на сегодня (31.10 по часам хоста) оно снято.
        var former = await world.Reports.EmployeeAsync("Белова Вера Викторовна");
        var (formerUser, gone) = await world.Reports.Assignments.UserAsync("Белова Вера Викторовна", WorkModule.ReportReview);
        await world.Reports.Assignments.LinkAsync(former, formerUser);
        await world.Reports.Assignments.AssignedAsync(former, world.SiteA, AssignmentKind.Manager, "2026-10-01", "2026-10-15");
        var id = Guid.NewGuid();
        var version = ReviewWorld.Version(await world.SubmittedAsync(petrov, id, world.SiteA));

        var accept = await ReviewWorld.AcceptAsync(alien, id, version);
        var back = await ReviewWorld.ReturnAsync(alien, id, version, "Объём");
        var missing = await ReviewWorld.AcceptAsync(alien, Guid.NewGuid(), version);
        var lapsed = await ReviewWorld.AcceptAsync(gone, id, version);

        Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, back.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, lapsed.StatusCode);
        Assert.Equal(await AssignmentWorld.ErrorAsync(missing), await AssignmentWorld.ErrorAsync(accept));
        Assert.Equal(ReportStatus.Submitted, (await world.Reports.ReportAsync(id))!.Status);
    }

    /// <summary>Право «на любых стройках» без назначений — приёмка любой стройки проходит.</summary>
    [Fact]
    public async Task Право_на_любых_стройках()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, admin) = await world.Reports.Assignments.UserAsync("Орлова Ольга Олеговна", WorkModule.ReportReview,
            WorkModule.SitesAll);
        var id = Guid.NewGuid();
        var version = ReviewWorld.Version(await world.SubmittedAsync(petrov, id, world.SiteB));

        var accept = await ReviewWorld.AcceptAsync(admin, id, version);

        Assert.Equal(HttpStatusCode.OK, accept.StatusCode);
        Assert.Equal(ReportStatus.Accepted, (await world.Reports.ReportAsync(id))!.Status);
    }
}
