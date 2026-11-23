using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Modules.Work.Review;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Очередь приёмки (change <c>work-16-review</c>; ТЗ WORK-28, WORK-39, WORK-46, CORE-24.3): отбор, пометки и их
/// счётчики по всему отбору, действия по праву, граница выдачи и причина пустоты. Сотрудники — вымышленные.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class ReviewQueueTests(AssignmentHost host) : IAsyncLifetime
{
    private const string Url = ReviewQueue.Path;
    private readonly ReviewWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Счётчик — по всему отбору, а не по странице: три правки после подачи при странице в две строки — «3» на обеих
    /// страницах. Все коды каталога приходят, нулевые тоже; отбор по пометке счётчиков не меняет.
    /// </summary>
    [Fact]
    public async Task Счётчик_по_всему_отбору()
    {
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        foreach (var name in new[] { "Петров Пётр Петрович", "Сидоров Семён Семёнович", "Козлов Константин Карпович" })
            await EditedAfterSubmitAsync(name, world.SiteA);
        var (_, plain) = await world.Reports.InstallerAsync("Морозов Матвей Михайлович");
        await world.SubmittedAsync(plain, Guid.NewGuid(), world.SiteA);

        var first = await QueueAsync(manager, "?limit=2");
        var second = await QueueAsync(manager, "?limit=2&offset=2");
        var marked = await QueueAsync(manager, $"?mark={ReviewMarks.EditedAfterSubmit}");

        Assert.Equal(4, first.GetProperty("total").GetInt32());
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.Equal(2, second.GetProperty("items").GetArrayLength());
        foreach (var page in new[] { first, second, marked })
        {
            var counts = page.GetProperty("counts");
            Assert.Equal(3, counts.GetProperty(ReviewMarks.EditedAfterSubmit).GetInt32());
            Assert.Equal(0, counts.GetProperty(ReviewMarks.LateDelivery).GetInt32());
            Assert.Equal(0, counts.GetProperty(ReviewMarks.PhotosPending).GetInt32());
            Assert.Equal(ReviewMarks.All.Select(m => m.Code), counts.EnumerateObject().Select(p => p.Name));
        }
        Assert.Equal(3, marked.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// Отбор по стройке и пометке — только поданные отчёты стройки А с этой пометкой; черновик и принятый в очередь не
    /// входят.
    /// </summary>
    [Fact]
    public async Task Отбор_по_стройке_и_пометке()
    {
        var (employee, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        await world.Reports.Assignments.AssignedAsync(employee, world.SiteB, AssignmentKind.Manager, "2026-10-01", null);
        var wanted = await EditedAfterSubmitAsync("Петров Пётр Петрович", world.SiteA);
        await EditedAfterSubmitAsync("Лебедев Лев Львович", world.SiteB);
        var (_, sidorov) = await world.Reports.InstallerAsync("Сидоров Семён Семёнович");
        await world.SubmittedAsync(sidorov, Guid.NewGuid(), world.SiteA);
        var (_, kozlov) = await world.Reports.InstallerAsync("Козлов Константин Карпович");
        Assert.Equal(HttpStatusCode.Created,
            (await kozlov.PostAsJsonAsync(ReportEndpoints.Path, world.Reports.Draft(Guid.NewGuid(), world.SiteA))).StatusCode);
        var (_, morozov) = await world.Reports.InstallerAsync("Морозов Матвей Михайлович");
        var acceptedId = Guid.NewGuid();
        var submitted = await world.SubmittedAsync(morozov, acceptedId, world.SiteA);
        Assert.Equal(HttpStatusCode.OK, (await ReviewWorld.AcceptAsync(manager, acceptedId, ReviewWorld.Version(submitted))).StatusCode);

        var all = await QueueAsync(manager, "");
        var page = await QueueAsync(manager, $"?constructionId={world.SiteA}&mark={ReviewMarks.EditedAfterSubmit}");

        Assert.Equal(3, all.GetProperty("total").GetInt32());
        var row = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(wanted, row.GetProperty("id").GetGuid());
        Assert.Equal("submitted", row.GetProperty("status").GetString());
        Assert.Equal([ReviewQueue.Accept, ReviewQueue.Return, ReviewQueue.Edit],
            row.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        Assert.Contains(ReviewMarks.EditedAfterSubmit, Codes(row));
    }

    /// <summary>Отбор по периоду и сотруднику: границы периода включены, отчёты вне периода и чужого сотрудника — нет.</summary>
    [Fact]
    public async Task Отбор_по_периоду_и_сотруднику()
    {
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (petrovId, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, sidorov) = await world.Reports.InstallerAsync("Сидоров Семён Семёнович");
        var (day15, day16) = (Guid.NewGuid(), Guid.NewGuid());
        await world.SubmittedAsync(petrov, day15, world.SiteA, date: "2026-11-15");
        await world.SubmittedAsync(petrov, day16, world.SiteA);
        await world.SubmittedAsync(sidorov, Guid.NewGuid(), world.SiteA);

        var from16 = await QueueAsync(manager, $"?from={ReportWorld.Day16}&employeeId={petrovId}");
        var to15 = await QueueAsync(manager, "?to=2026-11-15");

        Assert.Equal(day16, Assert.Single(from16.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(day15, Assert.Single(to15.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    /// <summary>Неизвестная пометка и перевёрнутый период — 400.</summary>
    [Fact]
    public async Task Неверный_отбор_отказ()
    {
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);

        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync($"{Url}?mark=nonsense")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync($"{Url}?from=2026-11-20&to=2026-11-10")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync($"{Url}?limit=0")).StatusCode);
    }

    /// <summary>
    /// Руководитель с одним <c>work.report.read</c> видит поданные отчёты всех строек, у строк <c>actions</c> пуст, граница —
    /// «все стройки». Монтажник с одним <c>work.report.own</c> — 403.
    /// </summary>
    [Fact]
    public async Task Руководитель_без_действий_монтажник_403()
    {
        var (_, head) = await world.Reports.Assignments.UserAsync("Смирнова Светлана Сергеевна", WorkModule.ReportRead);
        var (petrovId, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (sidorovId, sidorov) = await world.Reports.InstallerAsync("Сидоров Семён Семёнович");
        await world.SubmittedAsync(petrov, Guid.NewGuid(), world.SiteA);
        await world.SubmittedAsync(sidorov, Guid.NewGuid(), world.SiteB);

        var queue = await QueueAsync(head, "");
        var page = await QueueAsync(head, $"?employeeId={petrovId}&limit=1");

        Assert.Equal(2, queue.GetProperty("total").GetInt32());
        Assert.All(queue.GetProperty("items").EnumerateArray(), r => Assert.Equal(0, r.GetProperty("actions").GetArrayLength()));
        Assert.Equal("все стройки", queue.GetProperty("boundary").GetProperty("text").GetString());
        Assert.Equal("all", queue.GetProperty("boundary").GetProperty("code").GetString());
        Assert.Equal(petrovId, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("employeeId").GetGuid());
        Assert.Equal([(petrovId, "Петров Пётр Петрович"), (sidorovId, "Сидоров Семён Семёнович")],
            page.GetProperty("employees").EnumerateArray()
                .Select(person => (person.GetProperty("id").GetGuid(), person.GetProperty("name").GetString())));
        Assert.Equal(HttpStatusCode.Forbidden, (await petrov.GetAsync(Url)).StatusCode);
    }

    /// <summary>Отчёт чужой стройки в очереди менеджера отсутствует — ни строкой, ни числом.</summary>
    [Fact]
    public async Task Чужая_стройка_отсутствует()
    {
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (petrovId, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, sidorov) = await world.Reports.InstallerAsync("Сидоров Семён Семёнович");
        var own = Guid.NewGuid();
        await world.SubmittedAsync(petrov, own, world.SiteA);
        await world.SubmittedAsync(sidorov, Guid.NewGuid(), world.SiteB);

        var queue = await QueueAsync(manager, "");
        var other = await QueueAsync(manager, $"?constructionId={world.SiteB}");

        Assert.Equal(own, Assert.Single(queue.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(1, queue.GetProperty("total").GetInt32());
        Assert.Equal(0, other.GetProperty("total").GetInt32());
        Assert.Equal(0, other.GetProperty("counts").GetProperty(ReviewMarks.SiteNotAssigned).GetInt32());
        foreach (var page in new[] { queue, other })
            Assert.Equal(petrovId, Assert.Single(page.GetProperty("employees").EnumerateArray()).GetProperty("id").GetGuid());
    }

    /// <summary>
    /// Граница — «отчёты ваших строек (4)» у менеджера четырёх строек, и отбор по одной стройке числа не меняет.
    /// </summary>
    [Fact]
    public async Task Граница_не_зависит_от_отбора()
    {
        var (employee, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        await world.Reports.Assignments.AssignedAsync(employee, world.SiteB, AssignmentKind.Manager, "2026-10-01", null);
        foreach (var name in new[] { "Стройка В", "Стройка Г" })
            await world.Reports.Assignments.AssignedAsync(
                employee, await world.Reports.Assignments.SiteAsync(name), AssignmentKind.Manager, "2026-10-01", null);

        var all = await QueueAsync(manager, "");
        var one = await QueueAsync(manager, $"?constructionId={world.SiteA}");

        foreach (var page in new[] { all, one })
        {
            Assert.Equal("отчёты ваших строек (4)", page.GetProperty("boundary").GetProperty("text").GetString());
            Assert.Equal(4, page.GetProperty("boundary").GetProperty("sites").GetInt32());
        }
    }

    /// <summary>
    /// Пустая очередь называет причину: своих строек нет (назначений нет — или нет связи с сотрудником), ждущих нет,
    /// отбор исключил все.
    /// </summary>
    [Fact]
    public async Task Причина_пустой_очереди()
    {
        var (_, unlinked) = await world.Reports.Assignments.UserAsync("Белова Вера Викторовна", WorkModule.ReportReview);
        var orlova = await world.Reports.EmployeeAsync("Орлова Ольга Олеговна");
        var (orlovaUser, nobody) = await world.Reports.Assignments.UserAsync("Орлова Ольга Олеговна", WorkModule.ReportReview);
        await world.Reports.Assignments.LinkAsync(orlova, orlovaUser);
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);

        var notLinked = await QueueAsync(unlinked, "");
        var notAssigned = await QueueAsync(nobody, "");
        var nothing = await QueueAsync(manager, $"?employeeId={Guid.NewGuid()}");
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        await world.SubmittedAsync(petrov, Guid.NewGuid(), world.SiteA);
        var filtered = await QueueAsync(manager, $"?employeeId={Guid.NewGuid()}");
        var full = await QueueAsync(manager, "");

        Assert.Equal(ReviewQueue.NotLinked, notLinked.GetProperty("reason").GetString());
        Assert.Equal(ReviewQueue.NotAssigned, notAssigned.GetProperty("reason").GetString());
        Assert.Equal("отчёты ваших строек (0)", notAssigned.GetProperty("boundary").GetProperty("text").GetString());
        Assert.Equal(0, notAssigned.GetProperty("counts").GetProperty(ReviewMarks.LateDelivery).GetInt32());
        Assert.Equal(ReviewQueue.NothingPending, nothing.GetProperty("reason").GetString());
        Assert.Equal(ReviewQueue.FilteredOut, filtered.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, full.GetProperty("reason").ValueKind);
    }

    /// <summary>Нет справочника — причина о справочнике, а не об отсутствии назначения или связи.</summary>
    [Fact]
    public async Task Без_справочника_очередь_называет_причину()
    {
        await world.Reports.SeedAsync(classifier: false, directory: false);
        var (_, manager) = await world.Reports.Assignments.UserAsync("Менеджер", WorkModule.ReportReview);

        var page = await QueueAsync(manager, "");

        Assert.Empty(page.GetProperty("items").EnumerateArray());
        Assert.Equal(IModuleEmployees.NotSetUpCode, page.GetProperty("reason").GetString());
        Assert.Equal("sites", page.GetProperty("boundary").GetProperty("code").GetString());
        Assert.Equal(0, page.GetProperty("boundary").GetProperty("sites").GetInt32());
    }

    /// <summary>
    /// Пометки строки и ответа отчёта — одни: менеджер ввёл отчёт за сотрудника без назначения — у него
    /// <c>same-person</c>, <c>acceptWouldMark</c> и <c>site-not-assigned</c>; у другого менеджера той же стройки
    /// <c>same-person</c> нет. Правка менеджером — <c>manager-edit</c>, а не правка после подачи.
    /// </summary>
    [Fact]
    public async Task Пометки_строки_и_ответа_отчёта()
    {
        var (_, ivanova) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (_, kuznetsov) = await world.Reports.ManagerAsync("Кузнецов Кирилл Константинович", world.SiteA);
        var petrov = await world.Reports.EmployeeAsync("Петров Пётр Петрович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var draft = await ReportWorld.JsonAsync(await ivanova.PostAsJsonAsync(EntryReportEndpoints.Path,
            world.Reports.Draft(id, world.SiteA, employee: petrov, lineId: line)));
        var submitted = await ReportWorld.JsonAsync(await SubmissionTests.SubmitAsync(ivanova,
            $"{EntryReportEndpoints.Path}/{id}", ReviewWorld.Version(draft), ReviewWorld.Evening16));

        var mine = Assert.Single((await QueueAsync(ivanova, "")).GetProperty("items").EnumerateArray());
        var theirs = Assert.Single((await QueueAsync(kuznetsov, "")).GetProperty("items").EnumerateArray());
        Assert.True(mine.GetProperty("acceptWouldMark").GetBoolean());
        Assert.Equal([ReviewMarks.SamePerson, ReviewMarks.SiteNotAssigned], Codes(mine));
        Assert.Equal("стройка не назначена на дату", mine.GetProperty("marks")[1].GetProperty("text").GetString());
        Assert.Equal(Codes(mine), Codes(submitted));
        Assert.False(submitted.GetProperty("assignedOnDate").GetBoolean());
        Assert.False(theirs.GetProperty("acceptWouldMark").GetBoolean());
        Assert.Equal([ReviewMarks.SiteNotAssigned], Codes(theirs));

        var edited = await ReportWorld.PutAsync(kuznetsov, $"{EntryReportEndpoints.Path}/{id}", ReviewWorld.Version(submitted),
            world.Reports.Draft(id, world.SiteA, employee: petrov, lineId: line, volume: 90m, changeId: Guid.NewGuid(),
                deviceTime: "2026-11-16T19:00:00+03:00"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var after = Assert.Single((await QueueAsync(kuznetsov, "")).GetProperty("items").EnumerateArray());
        Assert.Equal([ReviewMarks.ManagerEdit, ReviewMarks.SiteNotAssigned], Codes(after));
        Assert.Equal(Codes(after), Codes(await ReviewWorld.AsManagerAsync(kuznetsov, id)));
    }

    /// <summary>
    /// Правка после подачи — о ПОСЛЕДНЕЙ подаче: после возврата и повторной подачи прежняя правка не помечает, новая —
    /// помечает.
    /// </summary>
    [Fact]
    public async Task Правка_после_подачи_о_последней_подаче()
    {
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var version = ReviewWorld.Version(await world.SubmittedAsync(petrov, id, world.SiteA, lineId: line));
        version = await EditAsync(petrov, id, line, version, 130m, "2026-11-16T19:00:00+03:00");
        Assert.Contains(ReviewMarks.EditedAfterSubmit, Codes(await RowAsync(manager)));

        var back = await ReviewWorld.ReturnAsync(manager, id, version, "Объём");
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        var again = await SubmissionTests.SubmitAsync(petrov, $"{ReportEndpoints.Path}/{id}",
            ReviewWorld.Version(await ReportWorld.JsonAsync(back)), ReviewWorld.Evening16.AddHours(3));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.DoesNotContain(ReviewMarks.EditedAfterSubmit, Codes(await RowAsync(manager)));

        await EditAsync(petrov, id, line, ReviewWorld.Version(await ReportWorld.JsonAsync(again)), 125m,
            "2026-11-16T22:00:00+03:00");
        Assert.Contains(ReviewMarks.EditedAfterSubmit, Codes(await RowAsync(manager)));
    }

    /// <summary>Поданный отчёт монтажника, поправленный им после подачи; идентификатор отчёта.</summary>
    private async Task<Guid> EditedAfterSubmitAsync(string name, Guid site)
    {
        var (_, installer) = await world.Reports.InstallerAsync(name);
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var submitted = await world.SubmittedAsync(installer, id, site, lineId: line);
        await EditAsync(installer, id, line, ReviewWorld.Version(submitted), 150m, "2026-11-16T19:00:00+03:00", site);
        return id;
    }

    private async Task<string?> EditAsync(
        HttpClient installer, Guid id, Guid line, string? version, decimal volume, string at, Guid? site = null)
    {
        var put = await ReportWorld.PutAsync(installer, $"{ReportEndpoints.Path}/{id}", version,
            world.Reports.Draft(id, site ?? world.SiteA, lineId: line, volume: volume, changeId: Guid.NewGuid(), deviceTime: at));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        return ReviewWorld.Version(await ReportWorld.JsonAsync(put));
    }

    private static async Task<JsonElement> RowAsync(HttpClient manager) =>
        Assert.Single((await QueueAsync(manager, "")).GetProperty("items").EnumerateArray());

    private static async Task<JsonElement> QueueAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync(Url + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReportWorld.JsonAsync(response);
    }

    private static List<string?> Codes(JsonElement report) =>
        [.. report.GetProperty("marks").EnumerateArray().Select(m => m.GetProperty("code").GetString())];
}
