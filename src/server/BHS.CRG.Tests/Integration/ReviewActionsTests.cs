using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Приёмка и возврат отчёта (change <c>work-16-review</c>; ТЗ WORK-9, WORK-11, WORK-13, WORK-14, WORK-31.1, WORK-56):
/// переходы статуса, журнал отчёта и ядра, правка менеджером, «ввёл и принял один человек» и уведомление о возврате.
/// Ворота и версия — <see cref="ReviewGateTests" />. Сотрудники — вымышленные.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class ReviewActionsTests(AssignmentHost host) : IAsyncLifetime
{
    private const string Url = ReportEndpoints.Path;
    private readonly ReviewWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Подан → принят: статус, запись журнала с автором, протокол и журнал действий ядра.</summary>
    [Fact]
    public async Task Подан_принят()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var id = Guid.NewGuid();
        var submitted = await world.SubmittedAsync(petrov, id, world.SiteA);

        var response = await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(submitted));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("accepted", (await ReportWorld.JsonAsync(response)).GetProperty("status").GetString());
        var entry = Assert.Single(await world.EntriesAsync(id), e => e.Kind == ReportJournalKinds.Accepted);
        Assert.Equal("Иванова Ирина Ивановна", entry.AuthorName);
        Assert.False(entry.AfterSubmission);
        Assert.Contains("accepted", entry.Changes);
        Assert.Contains(await world.Reports.ChangesAsync(id), c => c.Kind == ReportChangeKind.Accepted);
        Assert.Single(await world.CoreRecordsAsync(ReportActions.Accepted.Code, id));
        // Курсор отдаёт телефону новый статус.
        var feed = await ReportWorld.JsonAsync(await petrov.GetAsync($"{Url}?since=0"));
        Assert.Equal("accepted", Assert.Single(feed.GetProperty("items").EnumerateArray()).GetProperty("status").GetString());
    }

    /// <summary>
    /// Подан → возвращён → подан: комментарий остаётся в журнале, и отчёт снова ждёт приёмки. Колокольчик монтажника
    /// получает уведомление с комментарием, соседнего монтажника — нет.
    /// </summary>
    [Fact]
    public async Task Подан_возвращён_подан()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, sidorov) = await world.Reports.InstallerAsync("Сидоров Семён Семёнович");
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var id = Guid.NewGuid();
        var submitted = await world.SubmittedAsync(petrov, id, world.SiteA);

        var returned = await ReviewWorld.ReturnAsync(manager, id, ReviewWorld.Version(submitted), "  Уточните объём по оси 4  ");

        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        var body = await ReportWorld.JsonAsync(returned);
        Assert.Equal("returned", body.GetProperty("status").GetString());
        var entry = Assert.Single(await world.EntriesAsync(id), e => e.Kind == ReportJournalKinds.Returned);
        Assert.Equal("Уточните объём по оси 4", entry.Comment);
        Assert.Contains(await world.Reports.ChangesAsync(id), c => c.Kind == ReportChangeKind.Returned);
        Assert.Equal("Уточните объём по оси 4", Assert.Single(await world.CoreRecordsAsync(ReportActions.Returned.Code, id)).After);
        var bell = Assert.Single(await ReviewWorld.BellAsync(petrov));
        Assert.Equal("Отчёт за 16.11 возвращён", bell.Title);
        Assert.Contains("Уточните объём по оси 4", bell.Message);
        Assert.Contains("Иванова Ирина Ивановна", bell.Message);
        Assert.Empty(await ReviewWorld.BellAsync(sidorov));
        Assert.Empty(await ReviewWorld.BellAsync(manager));

        var again = await SubmissionTests.SubmitAsync(petrov, $"{Url}/{id}", ReviewWorld.Version(body), DateTimeOffset.UtcNow);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("submitted", (await ReportWorld.JsonAsync(again)).GetProperty("status").GetString());
        Assert.Single(await world.EntriesAsync(id), e => e.Kind == ReportJournalKinds.Returned && e.Comment != null);
    }

    /// <summary>Возврат без комментария — 400 «укажите, что исправить», статус прежний.</summary>
    [Fact]
    public async Task Возврат_без_комментария()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var id = Guid.NewGuid();
        var submitted = await world.SubmittedAsync(petrov, id, world.SiteA);

        var blank = await ReviewWorld.ReturnAsync(manager, id, ReviewWorld.Version(submitted), "   ");
        var none = await ReviewWorld.ReturnAsync(manager, id, ReviewWorld.Version(submitted), null);

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Contains("Укажите, что исправить", await AssignmentWorld.ErrorAsync(blank));
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(ReportStatus.Submitted, (await world.Reports.ReportAsync(id))!.Status);
    }

    /// <summary>
    /// Приёмка черновика и повторная приёмка принятого — 409 с кодом <c>report-status</c> и названием статуса, а не
    /// «отчёт тем временем изменили».
    /// </summary>
    [Fact]
    public async Task Отказ_по_статусу()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (draft, done) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, (await petrov.PostAsJsonAsync(Url, world.Reports.Draft(draft, world.SiteA))).StatusCode);
        var submitted = await world.SubmittedAsync(petrov, done, world.SiteA, date: "2026-11-15");
        var accepted = await ReportWorld.JsonAsync(await ReviewWorld.AcceptAsync(manager, done, ReviewWorld.Version(submitted)));

        var onDraft = await ReviewWorld.AcceptAsync(manager, draft, ReviewWorld.Version(await ReviewWorld.AsManagerAsync(manager, draft)));
        var twice = await ReviewWorld.ReturnAsync(manager, done, ReviewWorld.Version(accepted), "Поздно");

        Assert.Equal(HttpStatusCode.Conflict, onDraft.StatusCode);
        var body = await ReportWorld.JsonAsync(onDraft);
        Assert.Equal(ReportStatusException.ReasonCode, body.GetProperty("code").GetString());
        Assert.Equal("draft", body.GetProperty("status").GetString());
        Assert.Contains("«черновик»", body.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("accepted", (await ReportWorld.JsonAsync(twice)).GetProperty("status").GetString());
        Assert.Equal(ReportStatus.Draft, (await world.Reports.ReportAsync(draft))!.Status);
    }

    /// <summary>
    /// Два менеджера стройки принимают и возвращают один отчёт разом: решение одно, проигравший получает отказ по статусу
    /// («уже принят» или «возвращён»), а не «обновите страницу».
    /// </summary>
    [Fact]
    public async Task Два_решения_разом()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, ivanova) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (_, kuznetsov) = await world.Reports.ManagerAsync("Кузнецов Кирилл Константинович", world.SiteA);
        var id = Guid.NewGuid();
        var version = ReviewWorld.Version(await world.SubmittedAsync(petrov, id, world.SiteA));

        var answers = await Task.WhenAll(
            ReviewWorld.AcceptAsync(ivanova, id, version), ReviewWorld.ReturnAsync(kuznetsov, id, version, "Объём"));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        var lost = Assert.Single(answers, a => a.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(ReportStatusException.ReasonCode, (await ReportWorld.JsonAsync(lost)).GetProperty("code").GetString());
        Assert.Single(await world.EntriesAsync(id), e => e.Kind is ReportJournalKinds.Accepted or ReportJournalKinds.Returned);
    }

    /// <summary>
    /// Правка возвращённого — исправление по просьбе, а не «правка после подачи»; после повторной подачи правка снова
    /// помечается (ревью work-15, PR #1987).
    /// </summary>
    [Fact]
    public async Task Правка_возвращённого_не_после_подачи()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var submitted = await world.SubmittedAsync(petrov, id, world.SiteA, lineId: line);
        var returned = await ReportWorld.JsonAsync(await ReviewWorld.ReturnAsync(manager, id, ReviewWorld.Version(submitted), "Объём"));
        var later = DateTimeOffset.UtcNow.AddMinutes(-5);

        var fixedUp = await ReportWorld.JsonAsync(await ReportWorld.PutAsync(petrov, $"{Url}/{id}", ReviewWorld.Version(returned),
            world.Reports.Draft(id, world.SiteA, lineId: line, volume: 110m, changeId: Guid.NewGuid(),
                deviceTime: later.ToString("O"))));
        Assert.False((await world.EntriesAsync(id)).Last().AfterSubmission);

        var again = await ReportWorld.JsonAsync(await SubmissionTests.SubmitAsync(petrov, $"{Url}/{id}",
            ReviewWorld.Version(fixedUp), later.AddMinutes(1)));
        var edited = await ReportWorld.PutAsync(petrov, $"{Url}/{id}", ReviewWorld.Version(again),
            world.Reports.Draft(id, world.SiteA, lineId: line, volume: 105m, changeId: Guid.NewGuid(),
                deviceTime: later.AddMinutes(2).ToString("O")));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.True((await world.EntriesAsync(id)).Last().AfterSubmission);
    }

    /// <summary>
    /// Правка менеджером поданного отчёта (ТЗ WORK-14): запись журнала «правка менеджером» с прежним значением и его
    /// именем — её видит и монтажник; журнал действий ядра получает дубль. Черновик, введённый менеджером, правится
    /// обычной правкой.
    /// </summary>
    [Fact]
    public async Task Правка_менеджером()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var sidorov = await world.Reports.EmployeeAsync("Сидоров Семён Семёнович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var submitted = await world.SubmittedAsync(petrov, id, world.SiteA, lineId: line);

        var edited = await ReportWorld.PutAsync(manager, $"{EntryReportEndpoints.Path}/{id}", ReviewWorld.Version(submitted),
            world.Reports.Draft(id, world.SiteA, lineId: line, volume: 100m, changeId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var journal = await ReportWorld.JsonAsync(await petrov.GetAsync($"{Url}/{id}/journal"));
        var entry = journal.GetProperty("items").EnumerateArray().Last();
        Assert.Equal(ReportJournalKinds.ManagerEdited, entry.GetProperty("kind").GetString());
        Assert.Equal("Иванова Ирина Ивановна", entry.GetProperty("author").GetProperty("name").GetString());
        var volume = Assert.Single(entry.GetProperty("changes").EnumerateArray(), c => c.GetProperty("field").GetString() == "volume");
        Assert.Equal(120m, volume.GetProperty("before").GetDecimal());
        Assert.Equal(100m, volume.GetProperty("after").GetDecimal());
        Assert.Contains("volume", Assert.Single(await world.CoreRecordsAsync(ReportActions.ManagerEdited.Code, id)).After);
        Assert.Empty(await world.CoreRecordsAsync(ReportActions.EditedAfterSubmission.Code, id));

        var draftId = Guid.NewGuid();
        var draft = await ReportWorld.JsonAsync(await manager.PostAsJsonAsync(EntryReportEndpoints.Path,
            world.Reports.Draft(draftId, world.SiteA, employee: sidorov)));
        var own = await ReportWorld.PutAsync(manager, $"{EntryReportEndpoints.Path}/{draftId}", ReviewWorld.Version(draft),
            world.Reports.Draft(draftId, world.SiteA, employee: sidorov, hours: 6m, changeId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(ReportJournalKinds.Updated, (await world.EntriesAsync(draftId)).Last().Kind);
    }

    /// <summary>Правка принятого отчёта менеджером — тоже правка менеджером, а не обычная правка после подачи.</summary>
    [Fact]
    public async Task Правка_принятого_менеджером()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var submitted = await world.SubmittedAsync(petrov, id, world.SiteA, lineId: line);
        var accepted = await ReportWorld.JsonAsync(await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(submitted)));

        var edited = await ReportWorld.PutAsync(manager, $"{EntryReportEndpoints.Path}/{id}", ReviewWorld.Version(accepted),
            world.Reports.Draft(id, world.SiteA, lineId: line, volume: 90m, changeId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(ReportJournalKinds.ManagerEdited, (await world.EntriesAsync(id)).Last().Kind);
        Assert.Single(await world.CoreRecordsAsync(ReportActions.ManagerEdited.Code, id));
    }

    /// <summary>
    /// Повтор решения тем же <c>changeId</c> (ТЗ MOB-15) — 200 «повтор» без второй записи. Тот же <c>changeId</c> под
    /// другим решением — 409: возврат не выполнен, и ответ «повтор» был бы неправдой.
    /// </summary>
    [Fact]
    public async Task Повтор_решения_по_changeId()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var id = Guid.NewGuid();
        var version = ReviewWorld.Version(await world.SubmittedAsync(petrov, id, world.SiteA));
        var change = Guid.NewGuid();

        var first = await ReviewWorld.DecideAsync(manager, id, "accept", version, new { changeId = change });
        var again = await ReviewWorld.DecideAsync(manager, id, "accept", version, new { changeId = change });
        var other = await ReviewWorld.DecideAsync(manager, id, "return",
            ReviewWorld.Version(await ReportWorld.JsonAsync(first)), new { comment = "Объём", changeId = change });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var replayed = await ReportWorld.JsonAsync(again);
        Assert.True(replayed.GetProperty("replayed").GetBoolean());
        Assert.Equal("accepted", replayed.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Contains("changeId", (await ReportWorld.JsonAsync(other)).GetProperty("error").GetString());
        var entries = await world.EntriesAsync(id);
        Assert.Single(entries, e => e.Kind == ReportJournalKinds.Accepted);
        Assert.DoesNotContain(entries, e => e.Kind == ReportJournalKinds.Returned);
        Assert.Single(await world.CoreRecordsAsync(ReportActions.Accepted.Code, id));
    }

    /// <summary>
    /// «Ввёл и принял один человек» (ТЗ WORK-31.1): ввёл менеджер А — у А <c>acceptWouldMark: true</c>, у Б — <c>false</c>;
    /// приёмка А проходит, и журнал получает отдельную запись.
    /// </summary>
    [Fact]
    public async Task Ввёл_и_принял_один_человек()
    {
        var (_, ivanova) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (_, kuznetsov) = await world.Reports.ManagerAsync("Кузнецов Кирилл Константинович", world.SiteA);
        var petrov = await world.Reports.EmployeeAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var draft = await ReportWorld.JsonAsync(await ivanova.PostAsJsonAsync(EntryReportEndpoints.Path,
            world.Reports.Draft(id, world.SiteA, employee: petrov)));
        Assert.False(draft.GetProperty("acceptWouldMark").GetBoolean());
        var submitted = await ReportWorld.JsonAsync(await SubmissionTests.SubmitAsync(ivanova, $"{EntryReportEndpoints.Path}/{id}",
            ReviewWorld.Version(draft), ReviewWorld.Evening16));

        Assert.True(submitted.GetProperty("acceptWouldMark").GetBoolean());
        Assert.True((await ReviewWorld.AsManagerAsync(ivanova, id)).GetProperty("acceptWouldMark").GetBoolean());
        Assert.False((await ReviewWorld.AsManagerAsync(kuznetsov, id)).GetProperty("acceptWouldMark").GetBoolean());

        var accepted = await ReviewWorld.AcceptAsync(ivanova, id, ReviewWorld.Version(submitted));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var body = await ReportWorld.JsonAsync(accepted);
        Assert.Equal("accepted", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("acceptWouldMark").GetBoolean());
        var entries = await world.EntriesAsync(id);
        Assert.Equal([ReportJournalKinds.Accepted, ReportJournalKinds.SamePerson], entries.TakeLast(2).Select(e => e.Kind));
        Assert.Equal("Иванова Ирина Ивановна", entries[^1].AuthorName);
        Assert.Equal("[]", entries[^1].Changes);
        Assert.DoesNotContain(await world.Reports.ChangesAsync(id), c => c.Seq == entries[^1].Seq);
    }

    /// <summary>
    /// Возврат отчёта сотрудника без учётной записи (ввод за сотрудника): уведомить некого — журнал так и говорит, а
    /// колокольчик менеджера пуст.
    /// </summary>
    [Fact]
    public async Task Возврат_без_учётной_записи()
    {
        var (_, manager) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var petrov = await world.Reports.EmployeeAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var draft = await ReportWorld.JsonAsync(await manager.PostAsJsonAsync(EntryReportEndpoints.Path,
            world.Reports.Draft(id, world.SiteA, employee: petrov)));
        var submitted = await ReportWorld.JsonAsync(await SubmissionTests.SubmitAsync(manager, $"{EntryReportEndpoints.Path}/{id}",
            ReviewWorld.Version(draft), ReviewWorld.Evening16));

        var returned = await ReviewWorld.ReturnAsync(manager, id, ReviewWorld.Version(submitted), "Нет часов");

        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);
        var note = (await world.EntriesAsync(id)).Last();
        Assert.Equal(ReportJournalKinds.NotifyNone, note.Kind);
        Assert.Equal("уведомить некого: нет учётной записи", note.Comment);
        Assert.Empty(await ReviewWorld.BellAsync(manager));
    }
}
