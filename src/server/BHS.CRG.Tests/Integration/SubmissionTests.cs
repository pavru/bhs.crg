using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Ручная подача отчёта и окно подачи (change <c>work-15-submission</c>; ТЗ WORK-6, WORK-9, WORK-10, WORK-11, WORK-31,
/// WORK-52, MOB-35, AUTH-8, AUTH-9): статусы, версия, изоляция, обязательность, окно мгновением и ответ чтения.
/// Сотрудники — вымышленные. Время действия названо в каждом запросе: часы приложения в этом хосте настоящие.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class SubmissionTests(AssignmentHost host) : IAsyncLifetime
{
    private const string Url = ReportEndpoints.Path;
    private readonly ReportWorld world = new(host);

    private static readonly DateTimeOffset Evening16 = new(2026, 11, 16, 18, 0, 0, TimeSpan.FromHours(3));

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Дневной отчёт за 16.11, поданный в 18:00 16.11 по Москве: момент — 18:00, подал человек.</summary>
    [Fact]
    public async Task Подача_до_конца_окна()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var draft = await CreateAsync(client, world.Draft(id, world.SiteA));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 21, 0, 0, TimeSpan.Zero), draft.GetProperty("autoSubmitAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, draft.GetProperty("submittedAt").ValueKind);

        var response = await SubmitAsync(client, $"{Url}/{id}", Version(draft), Evening16);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReportWorld.JsonAsync(response);
        Assert.Equal("submitted", body.GetProperty("status").GetString());
        Assert.Equal(Evening16, body.GetProperty("submittedAt").GetDateTimeOffset());
        Assert.Equal("manual", body.GetProperty("submittedBy").GetString());
        Assert.False(body.GetProperty("alreadySubmitted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("autoSubmitAt").ValueKind);
        var entry = Assert.Single(await EntriesAsync(id), e => e.Kind == ReportJournalKinds.Submitted);
        Assert.Equal("Петров Пётр Петрович", entry.AuthorName);
        Assert.False(entry.AfterSubmission);
        Assert.Contains(await world.ChangesAsync(id), c => c.Kind == ReportChangeKind.Submitted);
    }

    /// <summary>Подача утром следующего дня — момент не позже конца окна (ТЗ WORK-10).</summary>
    [Fact]
    public async Task Подача_после_конца_окна_даёт_конец_окна()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var draft = await CreateAsync(client, world.Draft(id, world.SiteA));

        var body = await ReportWorld.JsonAsync(await SubmitAsync(client, $"{Url}/{id}", Version(draft),
            new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.FromHours(3))));

        Assert.Equal(new DateTimeOffset(2026, 11, 16, 21, 0, 0, TimeSpan.Zero), body.GetProperty("submittedAt").GetDateTimeOffset());
    }

    /// <summary>Без версии — 400; по устаревшей — 409, и отчёт остаётся черновиком.</summary>
    [Fact]
    public async Task Подача_без_версии_и_по_устаревшей()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var draft = await CreateAsync(client, world.Draft(id, world.SiteA, lineId: line));
        var put = await ReportWorld.PutAsync(client, $"{Url}/{id}", Version(draft),
            world.Draft(id, world.SiteA, lineId: line, volume: 150m, changeId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var bare = await SubmitAsync(client, $"{Url}/{id}", null, Evening16);
        var stale = await SubmitAsync(client, $"{Url}/{id}", Version(draft), Evening16);

        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ReportDesk.Stale, await AssignmentWorld.ErrorAsync(stale));
        Assert.Equal(ReportStatus.Draft, (await world.ReportAsync(id))!.Status);
    }

    /// <summary>Чужой отчёт — 404, как несуществующий; одно право приёмки без своих отчётов — 403.</summary>
    [Fact]
    public async Task Чужой_отчёт_и_право_приёмки()
    {
        var (_, petrov) = await world.InstallerAsync("Петров Пётр Петрович");
        var (_, sidorov) = await world.InstallerAsync("Сидоров Семён Семёнович");
        var (_, manager) = await world.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var id = Guid.NewGuid();
        var draft = await CreateAsync(petrov, world.Draft(id, world.SiteA));

        var alien = await SubmitAsync(sidorov, $"{Url}/{id}", Version(draft), Evening16);
        var reviewer = await SubmitAsync(manager, $"{Url}/{id}", Version(draft), Evening16);

        Assert.Equal(HttpStatusCode.NotFound, alien.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reviewer.StatusCode);
        Assert.Equal(ReportStatus.Draft, (await world.ReportAsync(id))!.Status);
    }

    /// <summary>Повтор подачи — 200 <c>alreadySubmitted</c> без сверки версии; второй подачи и записи журнала нет.</summary>
    [Fact]
    public async Task Повтор_подачи_отвечает_уже_подан()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var draft = await CreateAsync(client, world.Draft(id, world.SiteA));
        await SubmitAsync(client, $"{Url}/{id}", Version(draft), Evening16);

        var again = await SubmitAsync(client, $"{Url}/{id}", Version(draft), Evening16.AddHours(1));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var body = await ReportWorld.JsonAsync(again);
        Assert.True(body.GetProperty("alreadySubmitted").GetBoolean());
        Assert.Equal(Evening16, body.GetProperty("submittedAt").GetDateTimeOffset());
        Assert.Single(await EntriesAsync(id), e => e.Kind == ReportJournalKinds.Submitted);
    }

    /// <summary>Принятый отчёт не подаётся: 409 с названием статуса (ТЗ WORK-9).</summary>
    [Fact]
    public async Task Подача_принятого_отказ()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        await CreateAsync(client, world.Draft(id, world.SiteA));
        await StatusAsync(id, "accepted");

        var response = await SubmitAsync(client, $"{Url}/{id}", await VersionAsync(client, id), Evening16);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("принят", await AssignmentWorld.ErrorAsync(response));
    }

    /// <summary>
    /// Возвращённый подаётся снова: момент повторной подачи записан и окном не ограничен — позже конца окна 17.11. Часы
    /// приложения в тестах настоящие, поэтому момент берётся от них.
    /// </summary>
    [Fact]
    public async Task Повторная_подача_после_возврата()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        await CreateAsync(client, world.Draft(id, world.SiteA));
        await StatusAsync(id, "returned");
        var again = Seconds(DateTimeOffset.UtcNow.AddMinutes(-1));

        var body = await ReportWorld.JsonAsync(await SubmitAsync(client, $"{Url}/{id}", await VersionAsync(client, id), again));

        Assert.Equal("submitted", body.GetProperty("status").GetString());
        Assert.Equal(again, body.GetProperty("submittedAt").GetDateTimeOffset());
    }

    /// <summary>Телефон ушёл вперёд на сутки: возвращённый подан моментом часов сервера, а не «из будущего».</summary>
    [Fact]
    public async Task Повторная_подача_с_часами_впереди()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        await CreateAsync(client, world.Draft(id, world.SiteA));
        await StatusAsync(id, "returned");
        var before = DateTimeOffset.UtcNow;

        var body = await ReportWorld.JsonAsync(await SubmitAsync(client, $"{Url}/{id}", await VersionAsync(client, id),
            before.AddDays(1)));

        var at = body.GetProperty("submittedAt").GetDateTimeOffset();
        Assert.InRange(at, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    private static DateTimeOffset Seconds(DateTimeOffset at) =>
        new(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second, at.Offset);

    /// <summary>Смена поданного меняется только возвратом (ТЗ MOB-35): 409, отчёт прежний.</summary>
    [Fact]
    public async Task Смена_поданного_отказ()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var draft = await CreateAsync(client, world.Draft(id, world.SiteA, lineId: line));
        var submitted = await ReportWorld.JsonAsync(await SubmitAsync(client, $"{Url}/{id}", Version(draft), Evening16));

        var put = await ReportWorld.PutAsync(client, $"{Url}/{id}", Version(submitted),
            world.Draft(id, world.SiteA, lineId: line, shift: "night", changeId: Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal(Report.KeyLocked, await AssignmentWorld.ErrorAsync(put));
        Assert.Equal(ReportShift.Day, (await world.ReportAsync(id))!.Shift);
    }

    /// <summary>Правка объёма поданного до приёмки — разрешена автору и пишется правкой после подачи (ТЗ WORK-11).</summary>
    [Fact]
    public async Task Правка_строк_поданного_после_подачи()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var draft = await CreateAsync(client, world.Draft(id, world.SiteA, lineId: line));
        var submitted = await ReportWorld.JsonAsync(await SubmitAsync(client, $"{Url}/{id}", Version(draft), Evening16));

        var put = await ReportWorld.PutAsync(client, $"{Url}/{id}", Version(submitted), world.Draft(id, world.SiteA,
            lineId: line, volume: 150m, changeId: Guid.NewGuid(), deviceTime: "2026-11-17T09:00:00+03:00"));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await EntriesAsync(id)).Last().AfterSubmission);
    }

    /// <summary>Нет места у вида «требует места» — 422 со строкой и полем, отчёт — черновик; вид без требований проходит.</summary>
    [Fact]
    public async Task Обязательность_места_на_подаче()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (strict, free) = (Guid.NewGuid(), Guid.NewGuid());
        var a = await CreateAsync(client, world.Draft(strict, world.SiteA, workType: world.WorkTypeNeedsPlace, location: null));
        var b = await CreateAsync(client, world.Draft(free, world.SiteA, date: "2026-11-15", location: null));

        var refused = await SubmitAsync(client, $"{Url}/{strict}", Version(a), Evening16);
        var passed = await SubmitAsync(client, $"{Url}/{free}", Version(b), Evening16);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await ReportWorld.JsonAsync(refused);
        var missing = Assert.Single(body.GetProperty("requiredMissing").EnumerateArray());
        Assert.Equal(1, missing.GetProperty("line").GetInt32());
        Assert.Equal("location", missing.GetProperty("field").GetString());
        Assert.Contains("место работ", body.GetProperty("error").GetString());
        Assert.Equal(ReportStatus.Draft, (await world.ReportAsync(strict))!.Status);
        Assert.Equal(HttpStatusCode.OK, passed.StatusCode);
    }

    /// <summary>Ночной отчёт за 16.11 во Владивостоке (компания в Москве): окно — 00:00 18.11 по Владивостоку.</summary>
    [Fact]
    public async Task Ночное_окно_по_поясу_стройки()
    {
        var site = await world.Assignments.SiteAsync("Стройка В", "Asia/Vladivostok");
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");

        var draft = await CreateAsync(client, world.Draft(Guid.NewGuid(), site, shift: "night"));

        Assert.Equal(new DateTimeOffset(2026, 11, 17, 17, 0, 0, TimeSpan.FromHours(3)),
            draft.GetProperty("autoSubmitAt").GetDateTimeOffset());
    }

    /// <summary>Смена переключена на ночь — окно сдвигается с конца 16.11 на конец 17.11; правка часов окна не трогает.</summary>
    [Fact]
    public async Task Переключение_на_ночь_сдвигает_окно()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var draft = await CreateAsync(client, world.Draft(id, world.SiteA, lineId: line));

        var night = await ReportWorld.JsonAsync(await ReportWorld.PutAsync(client, $"{Url}/{id}", Version(draft),
            world.Draft(id, world.SiteA, lineId: line, shift: "night", changeId: Guid.NewGuid())));

        Assert.Equal(new DateTimeOffset(2026, 11, 17, 21, 0, 0, TimeSpan.Zero), night.GetProperty("autoSubmitAt").GetDateTimeOffset());
    }

    /// <summary>
    /// Пояс стройки исправили после заведения (Москва → Екатеринбург): окно прежнего черновика остаётся, правка часов
    /// его не пересчитывает, новый черновик — по новому поясу (ТЗ WORK-52).
    /// </summary>
    [Fact]
    public async Task Смена_пояса_задним_числом_окна_не_двигает()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var draft = await CreateAsync(client, world.Draft(id, world.SiteA, lineId: line));
        await ZoneAsync(world.SiteA, "Asia/Yekaterinburg");

        var edited = await ReportWorld.JsonAsync(await ReportWorld.PutAsync(client, $"{Url}/{id}", Version(draft),
            world.Draft(id, world.SiteA, lineId: line, hours: 6m, changeId: Guid.NewGuid())));
        var fresh = await CreateAsync(client, world.Draft(Guid.NewGuid(), world.SiteA, date: "2026-11-15"));

        Assert.Equal(new DateTimeOffset(2026, 11, 16, 21, 0, 0, TimeSpan.Zero), edited.GetProperty("autoSubmitAt").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 19, 0, 0, TimeSpan.Zero), fresh.GetProperty("autoSubmitAt").GetDateTimeOffset());
    }

    /// <summary>Менеджер подаёт введённый им за Петрова отчёт: подал человек, журнал называет менеджера (ТЗ WORK-31).</summary>
    [Fact]
    public async Task Менеджер_подаёт_введённый_отчёт()
    {
        var (_, manager) = await world.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var petrov = await world.EmployeeAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var created = await manager.PostAsJsonAsync(EntryReportEndpoints.Path, world.Draft(id, world.SiteA, employee: petrov));
        var draft = await ReportWorld.JsonAsync(created);

        var response = await SubmitAsync(manager, $"{EntryReportEndpoints.Path}/{id}", Version(draft), Evening16);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReportWorld.JsonAsync(response);
        Assert.Equal("submitted", body.GetProperty("status").GetString());
        Assert.Equal("manual", body.GetProperty("submittedBy").GetString());
        var entry = Assert.Single(await EntriesAsync(id), e => e.Kind == ReportJournalKinds.Submitted);
        Assert.Equal("Иванова Ирина Ивановна", entry.AuthorName);
        Assert.True(entry.EnteredFor);
    }

    /// <summary>
    /// Адрес ввода: чужая стройка — 404; без версии — 400; устаревшая — 409; монтажник с одним <c>work.report.own</c> —
    /// 403.
    /// </summary>
    [Fact]
    public async Task Подача_введённого_отказы()
    {
        var (_, manager) = await world.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (_, petrov) = await world.InstallerAsync("Петров Пётр Петрович");
        var sidorov = await world.EmployeeAsync("Сидоров Семён Семёнович");
        var (alien, own) = (Guid.NewGuid(), Guid.NewGuid());
        var other = await CreateAsync(petrov, world.Draft(alien, world.SiteB));
        var mine = await ReportWorld.JsonAsync(await manager.PostAsJsonAsync(EntryReportEndpoints.Path,
            world.Draft(own, world.SiteA, employee: sidorov)));
        var edited = await ReportWorld.PutAsync(manager, $"{EntryReportEndpoints.Path}/{own}", Version(mine),
            world.Draft(own, world.SiteA, employee: sidorov, hours: 7m, changeId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var entry = EntryReportEndpoints.Path;
        Assert.Equal(HttpStatusCode.NotFound, (await SubmitAsync(manager, $"{entry}/{alien}", Version(other), Evening16)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SubmitAsync(manager, $"{entry}/{own}", null, Evening16)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SubmitAsync(manager, $"{entry}/{own}", Version(mine), Evening16)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SubmitAsync(petrov, $"{entry}/{alien}", Version(other), Evening16)).StatusCode);
        Assert.Equal(ReportStatus.Draft, (await world.ReportAsync(own))!.Status);
    }

    /// <summary>
    /// «Мои стройки» в 03:00 01.11 по Владивостоку: есть ночной отчёт за 31.10 — «вчера · ночь», а <c>today</c> — 01.11;
    /// на московской стройке (20:00 31.10) ночного нет — «сегодня · день».
    /// </summary>
    [Fact]
    public async Task Предлагаемая_пара_в_моих_стройках()
    {
        var east = await world.Assignments.SiteAsync("Стройка Восток", "Asia/Vladivostok");
        var (employee, client) = await world.InstallerAsync("Петров Пётр Петрович");
        await world.Assignments.AssignedAsync(employee, east, AssignmentKind.Installer, "2026-10-01", "2026-12-31");
        await world.Assignments.AssignedAsync(employee, world.SiteA, AssignmentKind.Installer, "2026-10-01", "2026-12-31");
        await CreateAsync(client, world.Draft(Guid.NewGuid(), east, date: "2026-10-31", shift: "night"));

        var items = (await ReportWorld.JsonAsync(await client.GetAsync("/api/work/sites/mine"))).GetProperty("items")
            .EnumerateArray().ToDictionary(i => i.GetProperty("id").GetGuid());

        var night = items[east];
        Assert.Equal("2026-11-01", night.GetProperty("today").GetString());
        Assert.Equal("2026-10-31", night.GetProperty("suggested").GetProperty("date").GetString());
        Assert.Equal("night", night.GetProperty("suggested").GetProperty("shift").GetString());
        Assert.Equal("Ночная смена 31.10", night.GetProperty("suggested").GetProperty("label").GetString());
        var day = items[world.SiteA].GetProperty("suggested");
        Assert.Equal("2026-10-31", day.GetProperty("date").GetString());
        Assert.Equal("day", day.GetProperty("shift").GetString());
        Assert.Equal("Отчёт 31.10", day.GetProperty("label").GetString());
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync(Url, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReportWorld.JsonAsync(response);
    }

    internal static Task<HttpResponseMessage> SubmitAsync(HttpClient client, string reportUrl, string? version, DateTimeOffset at) =>
        client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Post, $"{reportUrl}/submit", version,
            new { changeId = Guid.NewGuid(), deviceTime = at }));

    private static string? Version(JsonElement report) => report.GetProperty("version").GetString();

    private static async Task<string?> VersionAsync(HttpClient client, Guid id) =>
        Version(await ReportWorld.JsonAsync(await client.GetAsync($"{Url}/{id}")));

    /// <summary>Статус мимо адресов — подготовка: приёмку и возврат адресами проверяет <c>ReviewActionsTests</c>.</summary>
    private async Task StatusAsync(Guid id, string status)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE work.reports SET status = {status}, submitted_at = now(), submitted_by = 'manual' WHERE id = {id}"));
    }

    private async Task ZoneAsync(Guid siteId, string zone)
    {
        using var scope = host.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<Construction>>();
        var site = await repo.GetByIdAsync(siteId);
        site!.SetTimeZone(zone);
        await repo.SaveChangesAsync();
    }

    private async Task<List<ReportJournalEntry>> EntriesAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Set<ReportJournalEntry>().AsNoTracking()
            .Where(e => e.ReportId == id).OrderBy(e => e.Seq).ToListAsync();
    }
}
