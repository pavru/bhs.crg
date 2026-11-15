using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Свои отчёты монтажника (<c>/api/work/reports</c>; change <c>work-13-report-model</c>, ТЗ WORK-39, MOB-15, MOB-16,
/// MOB-17, MOB-21, AUTH-8): сотрудник из связи, чужой отчёт — тот же 404, протокол повтора и курсор.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class ReportEndpointsTests(AssignmentHost host) : IAsyncLifetime
{
    private const string Url = ReportEndpoints.Path;
    private readonly ReportWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Ответ на заведение потерялся, клиент повторил тот же <c>id</c> — 200 с тем же отчётом, второго нет (MOB-15).</summary>
    [Fact]
    public async Task Повтор_заведения_возвращает_тот_же_отчёт()
    {
        var (employee, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var body = world.Draft(id, world.SiteA);

        var first = await client.PostAsJsonAsync(Url, body);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var created = await ReportWorld.JsonAsync(first);
        Assert.Equal(employee, created.GetProperty("employeeId").GetGuid());
        Assert.False(created.GetProperty("replayed").GetBoolean());

        var again = await client.PostAsJsonAsync(Url, body);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var replayed = await ReportWorld.JsonAsync(again);
        Assert.True(replayed.GetProperty("replayed").GetBoolean());
        Assert.Equal(id, replayed.GetProperty("id").GetGuid());
        Assert.Equal(1, await world.ReportCountAsync());
        Assert.Single(await world.ChangesAsync(id));
    }

    /// <summary>Идентификатор занят отчётом другого сотрудника — 409 «занят» без полей того отчёта (MOB-15).</summary>
    [Fact]
    public async Task Чужой_идентификатор_отказ_без_полей()
    {
        var (_, a) = await world.InstallerAsync("Петров Пётр Петрович");
        var (_, b) = await world.InstallerAsync("Сидоров Семён Семёнович");
        var id = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await b.PostAsJsonAsync(Url, world.Draft(id, world.SiteB))).StatusCode);

        var response = await a.PostAsJsonAsync(Url, world.Draft(id, world.SiteA));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReportWorld.JsonAsync(response);
        Assert.Equal(ReportDesk.IdTaken, body.GetProperty("error").GetString());
        Assert.False(body.TryGetProperty("constructionId", out _));
        Assert.False(body.TryGetProperty("existingId", out _));
        Assert.Equal(1, await world.ReportCountAsync());
    }

    /// <summary>Чужой отчёт по идентификатору — тот же 404, что несуществующий; правка и удаление его не трогают (WORK-39).</summary>
    [Fact]
    public async Task Чужой_отчёт_неотличим_от_несуществующего()
    {
        var (_, a) = await world.InstallerAsync("Петров Пётр Петрович");
        var (_, b) = await world.InstallerAsync("Сидоров Семён Семёнович");
        var id = Guid.NewGuid();
        var created = await ReportWorld.JsonAsync(await b.PostAsJsonAsync(Url, world.Draft(id, world.SiteB)));
        var version = created.GetProperty("version").GetString();

        var foreign = await a.GetAsync($"{Url}/{id}");
        var missing = await a.GetAsync($"{Url}/{Guid.NewGuid()}");
        var put = await ReportWorld.PutAsync(a, $"{Url}/{id}", version,
            world.Draft(id, world.SiteB, hours: 2m, changeId: Guid.NewGuid()));
        var delete = await ReportWorld.DeleteAsync(a, $"{Url}/{id}", version);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(await AssignmentWorld.ErrorAsync(missing), await AssignmentWorld.ErrorAsync(foreign));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        var stored = await world.ReportAsync(id);
        Assert.NotNull(stored);
        Assert.Equal(8m, stored.Hours);
    }

    /// <summary>Тело называет другого сотрудника — 400: сотрудника отчёта задаёт связь учётной записи (WORK-39).</summary>
    [Fact]
    public async Task Сотрудник_в_теле_отвергается()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var other = await world.EmployeeAsync("Сидоров Семён Семёнович");

        var response = await client.PostAsJsonAsync(Url, world.Draft(Guid.NewGuid(), world.SiteA, employee: other));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("employeeId", await AssignmentWorld.ErrorAsync(response));
        Assert.Equal(0, await world.ReportCountAsync());
    }

    /// <summary>Учётная запись без сотрудника — 403 с причиной, а не пустой список (WORK-39).</summary>
    [Fact]
    public async Task Учётная_запись_без_сотрудника_отказ_с_причиной()
    {
        var (_, client) = await world.Assignments.UserAsync("Без связи", WorkModule.ReportOwn);

        var list = await client.GetAsync(Url);
        var create = await client.PostAsJsonAsync(Url, world.Draft(Guid.NewGuid(), world.SiteA));

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(ReportEndpoints.NotLinked, await AssignmentWorld.ErrorAsync(list));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
    }

    /// <summary>Справочника сотрудников нет — 409 «справочник не заведён» с кодом, а не 403 «не связана» (CORE-20.4).</summary>
    [Fact]
    public async Task Без_справочника_сотрудников_отказ_справочника()
    {
        await world.SeedAsync(classifier: false, directory: false);
        var (_, client) = await world.Assignments.UserAsync("Монтажник", WorkModule.ReportOwn);

        var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var refusal = await ReportWorld.JsonAsync(response);
        Assert.Equal(IModuleEmployees.NotSetUpCode, refusal.GetProperty("code").GetString());
        Assert.Equal(IModuleEmployees.NotSetUp, refusal.GetProperty("error").GetString());
    }

    /// <summary>Часы в строке — 400 с номером строки и названием поля, а не молча пропавшее поле (WORK-7).</summary>
    [Fact]
    public async Task Часы_в_строке_тела_отвергаются()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var body = new
        {
            id = Guid.NewGuid(), constructionId = world.SiteA, date = ReportWorld.Day16,
            lines = new[] { new { id = Guid.NewGuid(), workTypeId = world.WorkType, volume = 1m, unitId = world.Unit, hours = 4 } },
        };

        var response = await client.PostAsJsonAsync(Url, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await AssignmentWorld.ErrorAsync(response);
        Assert.Contains("Строка 1", error);
        Assert.Contains("hours", error);
    }

    /// <summary>
    /// Правка: без <c>If-Match</c> — 400; устаревшая версия с новым <c>changeId</c> — 409; повтор применённого
    /// <c>changeId</c> с прежней версией — 200 с результатом той правки, без второго применения (MOB-15).
    /// </summary>
    [Fact]
    public async Task Правка_версия_и_повтор_изменения()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var v1 = (await ReportWorld.JsonAsync(await client.PostAsJsonAsync(Url, world.Draft(id, world.SiteA))))
            .GetProperty("version").GetString();
        var change = Guid.NewGuid();

        var unversioned = await ReportWorld.PutAsync(client, $"{Url}/{id}", null, world.Draft(id, world.SiteA, 9m, changeId: change));
        Assert.Equal(HttpStatusCode.BadRequest, unversioned.StatusCode);

        var applied = await ReportWorld.PutAsync(client, $"{Url}/{id}", v1, world.Draft(id, world.SiteA, 9m, changeId: change));
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var v2 = (await ReportWorld.JsonAsync(applied)).GetProperty("version").GetString();
        Assert.NotEqual(v1, v2);

        var stale = await ReportWorld.PutAsync(client, $"{Url}/{id}", v1,
            world.Draft(id, world.SiteA, 10m, changeId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ReportDesk.Stale, await AssignmentWorld.ErrorAsync(stale));

        var replay = await ReportWorld.PutAsync(client, $"{Url}/{id}", v1, world.Draft(id, world.SiteA, 9m, changeId: change));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = await ReportWorld.JsonAsync(replay);
        Assert.True(replayed.GetProperty("replayed").GetBoolean());
        Assert.Equal(v2, replayed.GetProperty("version").GetString());
        Assert.Equal(9m, replayed.GetProperty("hours").GetDecimal());
        Assert.Equal(2, (await world.ChangesAsync(id)).Count);
    }

    /// <summary>
    /// Часы телефона ушли назад — номер всё равно больше, и <c>since</c> отдаёт правку; удалённый черновик — в списке
    /// удалённых; повтор удаления с тем же <c>changeId</c> — 204 (MOB-16).
    /// </summary>
    [Fact]
    public async Task Курсор_по_серверной_последовательности_и_удалённые()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (kept, dropped) = (Guid.NewGuid(), Guid.NewGuid());
        var keptBody = await ReportWorld.JsonAsync(await client.PostAsJsonAsync(Url,
            world.Draft(kept, world.SiteA, deviceTime: "2026-11-16T18:00:00+03:00")));
        var droppedBody = await ReportWorld.JsonAsync(await client.PostAsJsonAsync(Url,
            world.Draft(dropped, world.SiteA, shift: "night")));
        var cursor = droppedBody.GetProperty("seq").GetInt64();

        var past = await ReportWorld.PutAsync(client, $"{Url}/{kept}", keptBody.GetProperty("version").GetString(),
            world.Draft(kept, world.SiteA, 7m, changeId: Guid.NewGuid(), deviceTime: "2026-11-16T09:00:00+03:00"));
        Assert.Equal(HttpStatusCode.OK, past.StatusCode);
        var pastSeq = (await ReportWorld.JsonAsync(past)).GetProperty("seq").GetInt64();
        Assert.True(pastSeq > cursor);

        var deleteChange = Guid.NewGuid();
        var delete = await client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Delete,
            $"{Url}/{dropped}?changeId={deleteChange}", droppedBody.GetProperty("version").GetString()));
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var again = await client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Delete,
            $"{Url}/{dropped}?changeId={deleteChange}", droppedBody.GetProperty("version").GetString()));
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);

        var feed = await ReportWorld.JsonAsync(await client.GetAsync($"{Url}?since={cursor}"));

        var item = Assert.Single(feed.GetProperty("items").EnumerateArray());
        Assert.Equal(kept, item.GetProperty("id").GetGuid());
        var gone = Assert.Single(feed.GetProperty("deleted").EnumerateArray());
        Assert.Equal(dropped, gone.GetProperty("id").GetGuid());
        Assert.True(gone.GetProperty("seq").GetInt64() > pastSeq);
        Assert.Equal(gone.GetProperty("seq").GetInt64(), feed.GetProperty("cursor").GetInt64());
        Assert.False(feed.GetProperty("more").GetBoolean());

        var next = await ReportWorld.JsonAsync(await client.GetAsync($"{Url}?since={feed.GetProperty("cursor").GetInt64()}"));
        Assert.Empty(next.GetProperty("items").EnumerateArray());
        Assert.Empty(next.GetProperty("deleted").EnumerateArray());
    }

    /// <summary>Время устройства на час вперёд — правка принята, оба времени сохранены (MOB-17).</summary>
    [Fact]
    public async Task Время_устройства_впереди_принято_оба_времени_сохранены()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var ahead = DateTimeOffset.UtcNow.AddHours(1);

        var response = await client.PostAsJsonAsync(Url, world.Draft(id, world.SiteA, deviceTime: ahead.ToString("O")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var change = Assert.Single(await world.ChangesAsync(id));
        Assert.Equal(ahead.ToUnixTimeMilliseconds(), change.DeviceTime!.Value.ToUnixTimeMilliseconds());
        Assert.True(change.ServerTime < ahead.AddMinutes(-30));
        Assert.Equal(ReportChangeKind.Created, change.Kind);
    }

    /// <summary>Назначение кончилось вчера — отчёт сохранён, ответ несёт <c>assignedOnDate: false</c>; назначенный — <c>true</c> (MOB-21).</summary>
    [Fact]
    public async Task Стройка_без_назначения_на_дату_пометка_а_не_отказ()
    {
        var (employee, client) = await world.InstallerAsync("Петров Пётр Петрович");
        await world.Assignments.AssignedAsync(employee, world.SiteA, AssignmentKind.Installer, "2026-11-01", "2026-11-15");

        var ended = await client.PostAsJsonAsync(Url, world.Draft(Guid.NewGuid(), world.SiteA));
        var covered = await client.PostAsJsonAsync(Url, world.Draft(Guid.NewGuid(), world.SiteA, date: "2026-11-15"));

        Assert.Equal(HttpStatusCode.Created, ended.StatusCode);
        Assert.False((await ReportWorld.JsonAsync(ended)).GetProperty("assignedOnDate").GetBoolean());
        Assert.True((await ReportWorld.JsonAsync(covered)).GetProperty("assignedOnDate").GetBoolean());
    }

    /// <summary>Классификатора видов работ нет — заведение строки 409 «справочник не заведён» с кодом (CORE-20.4).</summary>
    [Fact]
    public async Task Без_классификатора_заведение_строки_отказ_справочника()
    {
        await world.SeedAsync(classifier: false);
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var body = new
        {
            id = Guid.NewGuid(), constructionId = world.SiteA, date = ReportWorld.Day16,
            lines = new[] { new { id = Guid.NewGuid(), workTypeId = Guid.NewGuid(), volume = 1m, unitId = Guid.NewGuid() } },
        };

        var response = await client.PostAsJsonAsync(Url, body);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var refusal = await ReportWorld.JsonAsync(response);
        Assert.Equal(IModuleEmployees.NotSetUpCode, refusal.GetProperty("code").GetString());
        Assert.Equal(IModuleWorkTypes.NotSetUp, refusal.GetProperty("error").GetString());
    }

    /// <summary>Четвёрка занята своим же отчётом — 409 с идентификатором существующего, чтобы клиент открыл его.</summary>
    [Fact]
    public async Task Занятая_четвёрка_называет_существующий_отчёт()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var first = Guid.NewGuid();
        await client.PostAsJsonAsync(Url, world.Draft(first, world.SiteA));

        var response = await client.PostAsJsonAsync(Url, world.Draft(Guid.NewGuid(), world.SiteA));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(first, (await ReportWorld.JsonAsync(response)).GetProperty("existingId").GetGuid());
    }

    /// <summary>Право чтения всех отчётов без своих — 403 на заведении (AUTH-8).</summary>
    [Fact]
    public async Task Без_права_своих_отчётов_403()
    {
        var (_, client) = await world.Assignments.UserAsync("Руководитель", WorkModule.ReportRead);

        var response = await client.PostAsJsonAsync(Url, world.Draft(Guid.NewGuid(), world.SiteA));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
