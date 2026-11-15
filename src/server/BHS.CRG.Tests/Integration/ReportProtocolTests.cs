using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Протокол повтора отчёта на краях (change <c>work-13-report-model</c>, ревью PR #1976; ТЗ MOB-15, MOB-16, MOB-17):
/// удалённый идентификатор, повтор после чужой правки, очерёдность номеров одного сотрудника и гонка двух заведений.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class ReportProtocolTests(AssignmentHost host) : IAsyncLifetime
{
    private const string Url = ReportEndpoints.Path;
    private readonly ReportWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Опоздавший повтор заведения после удаления черновика — 409 «удалён», а не воскресший отчёт; с прежним
    /// <c>changeId</c> — тот же 409, а не 500 на уникальном индексе протокола. Чужой сотрудник — «занят».
    /// </summary>
    [Fact]
    public async Task Удалённый_идентификатор_не_заводится_снова()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (_, other) = await world.InstallerAsync("Сидоров Семён Семёнович");
        var (id, change) = (Guid.NewGuid(), Guid.NewGuid());
        var body = world.Draft(id, world.SiteA, changeId: change);
        var created = await ReportWorld.JsonAsync(await client.PostAsJsonAsync(Url, body));
        Assert.Equal(HttpStatusCode.NoContent,
            (await ReportWorld.DeleteAsync(client, $"{Url}/{id}", created.GetProperty("version").GetString())).StatusCode);

        var late = await client.PostAsJsonAsync(Url, body);
        var fresh = await client.PostAsJsonAsync(Url, world.Draft(id, world.SiteA, changeId: Guid.NewGuid()));
        var foreign = await other.PostAsJsonAsync(Url, world.Draft(id, world.SiteB));

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal(ReportDesk.Deleted, await AssignmentWorld.ErrorAsync(late));
        Assert.Equal(HttpStatusCode.Conflict, fresh.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
        Assert.Equal(ReportDesk.IdTaken, await AssignmentWorld.ErrorAsync(foreign));
        Assert.Equal(0, await world.ReportCountAsync());
    }

    /// <summary>
    /// Повтор правки C после более поздней правки D — тело и версия одного состояния (D), а не тело D с версией C:
    /// следующая правка клиента по этой версии проходит.
    /// </summary>
    [Fact]
    public async Task Повтор_правки_после_чужой_отдаёт_согласованные_тело_и_версию()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var v1 = (await ReportWorld.JsonAsync(await client.PostAsJsonAsync(Url, world.Draft(id, world.SiteA))))
            .GetProperty("version").GetString();
        var c = Guid.NewGuid();
        var v2 = (await ReportWorld.JsonAsync(await ReportWorld.PutAsync(client, $"{Url}/{id}", v1,
            world.Draft(id, world.SiteA, 9m, changeId: c)))).GetProperty("version").GetString();
        var v3 = (await ReportWorld.JsonAsync(await ReportWorld.PutAsync(client, $"{Url}/{id}", v2,
            world.Draft(id, world.SiteA, 6m, changeId: Guid.NewGuid())))).GetProperty("version").GetString();

        var replay = await ReportWorld.JsonAsync(await ReportWorld.PutAsync(client, $"{Url}/{id}", v1,
            world.Draft(id, world.SiteA, 9m, changeId: c)));

        Assert.True(replay.GetProperty("replayed").GetBoolean());
        Assert.Equal(6m, replay.GetProperty("hours").GetDecimal());
        Assert.Equal(v3, replay.GetProperty("version").GetString());
        var next = await ReportWorld.PutAsync(client, $"{Url}/{id}", replay.GetProperty("version").GetString(),
            world.Draft(id, world.SiteA, 5m, changeId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    /// <summary>
    /// Записи одного сотрудника идут по очереди под его замком (MOB-16): пока замок держит другая транзакция, запись
    /// ждёт и получает номер после. Два заведения одного <c>id</c>, прошедшие проверку «занят ли» разом, после замка
    /// решаются уникальностью — 201 и повтор 200, отчёт один.
    /// </summary>
    [Fact]
    public async Task Записи_сотрудника_по_очереди_и_гонка_заведения_решается_повтором()
    {
        var (employee, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var body = world.Draft(id, world.SiteA);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        Task<HttpResponseMessage>[] posts;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var held = await db.NextReportSeqAsync(employee, default);
            posts = [client.PostAsJsonAsync(Url, body), client.PostAsJsonAsync(Url, body)];
            await WaitForLockWaitersAsync(2);
            Assert.All(posts, p => Assert.False(p.IsCompleted, "Запись сотрудника прошла мимо его замка."));
            await tx.CommitAsync();

            var responses = await Task.WhenAll(posts);
            var codes = responses.Select(r => r.StatusCode).Order().ToList();
            Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Created], codes);
            var replayed = await ReportWorld.JsonAsync(responses.Single(r => r.StatusCode == HttpStatusCode.OK));
            Assert.True(replayed.GetProperty("replayed").GetBoolean());
            var won = await ReportWorld.JsonAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Created));
            Assert.True(won.GetProperty("seq").GetInt64() > held);
        }
        Assert.Equal(1, await world.ReportCountAsync());
    }

    /// <summary>Время устройства в строке запроса удаления с незакодированным «+» — принято и приведено к UTC (MOB-17).</summary>
    [Fact]
    public async Task Время_устройства_у_удаления_с_плюсом_принято()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var version = (await ReportWorld.JsonAsync(await client.PostAsJsonAsync(Url, world.Draft(id, world.SiteA))))
            .GetProperty("version").GetString();

        var response = await ReportWorld.DeleteAsync(client, $"{Url}/{id}?deviceTime=2026-11-16T18:00:00+03:00", version);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var gone = (await world.ChangesAsync(id)).Single(c => c.Kind == ReportChangeKind.Deleted);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 15, 0, 0, TimeSpan.Zero), gone.DeviceTime);
    }

    /// <summary>Ждать, пока на замках сотрудников не повиснут <paramref name="count" /> записей.</summary>
    private async Task WaitForLockWaitersAsync(int count)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        for (var i = 0; i < 200; i++)
        {
            var waiting = await db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND NOT granted")
                .SingleAsync();
            if (waiting >= count) return;
            await Task.Delay(50);
        }
        Assert.Fail($"За 10 с на замке не повисло {count} записей: проверка очерёдности шла бы вхолостую.");
    }
}
