using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Modules.Work.Submission;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Автоподача (change <c>work-15-submission</c>; ТЗ WORK-10, WORK-41, WORK-6, WORK-11, WORK-54, CORE-24.1, CORE-28):
/// по состоянию, а не по расписанию, с моментом конца окна, от имени системы. Задача создаётся тестом с подставными
/// часами и выполняется вызовом — такт ядра проверяет <see cref="ModuleTickServiceTests" />. Сотрудники — вымышленные.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class AutoSubmitTests(AssignmentHost host) : IAsyncLifetime
{
    private const string Url = ReportEndpoints.Path;
    private readonly ReportWorld world = new(host);

    /// <summary>Сервер поднялся в полдень 19.11 по Москве после простоя с 16.11.</summary>
    private static readonly DateTimeOffset Up19 = new(2026, 11, 19, 12, 0, 0, TimeSpan.FromHours(3));

    private static DateTimeOffset Midnight(int day) => new(2026, 11, day, 0, 0, 0, TimeSpan.FromHours(3));

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Простой три дня: каждый черновик с истёкшим окном подан моментом своего окна, а не временем подъёма; черновик с
    /// открытым окном остаётся; в журнал ядра — одна запись на прогон.
    /// </summary>
    [Fact]
    public async Task Простой_три_дня()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var day16 = await CreateAsync(client, "2026-11-16", "day");
        var night16 = await CreateAsync(client, "2026-11-16", "night");
        var day17 = await CreateAsync(client, "2026-11-17", "day");
        var day18 = await CreateAsync(client, "2026-11-18", "day");
        var day19 = await CreateAsync(client, "2026-11-19", "day");

        await RunAsync(Up19);

        Assert.Equal((Midnight(17), ReportSubmitter.Auto), await SubmittedAsync(day16));
        Assert.Equal((Midnight(18), ReportSubmitter.Auto), await SubmittedAsync(night16));
        Assert.Equal((Midnight(18), ReportSubmitter.Auto), await SubmittedAsync(day17));
        Assert.Equal((Midnight(19), ReportSubmitter.Auto), await SubmittedAsync(day18));
        Assert.Equal(ReportStatus.Draft, (await world.ReportAsync(day19))!.Status);

        var run = Assert.Single(await CoreRecordsAsync());
        Assert.Equal("подано отчётов: 4", run.After);
    }

    /// <summary>
    /// Запись журнала отчёта: автор «Система», вид «подан автоматически», момент — конец окна; это не ввод за
    /// сотрудника и не правка после подачи. Курсор отдаёт телефону новый статус.
    /// </summary>
    [Fact]
    public async Task Запись_журнала_автоподачи()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = await CreateAsync(client, "2026-11-16", "day");

        await RunAsync(Up19);

        var entry = Assert.Single(await EntriesAsync(id), e => e.Kind == ReportJournalKinds.AutoSubmitted);
        Assert.Equal(AutoSubmitJob.SystemAuthor, entry.AuthorName);
        Assert.Null(entry.AuthorId);
        Assert.False(entry.EnteredFor);
        Assert.False(entry.AfterSubmission);
        Assert.Contains("submittedAt", entry.Changes);
        var feed = await ReportWorld.JsonAsync(await client.GetAsync($"{Url}?since=0"));
        Assert.Equal("submitted", Assert.Single(feed.GetProperty("items").EnumerateArray()).GetProperty("status").GetString());
    }

    /// <summary>Два прогона разом — каждый отчёт подан один раз и имеет одну запись журнала о подаче.</summary>
    [Fact]
    public async Task Два_экземпляра_такта()
    {
        var (_, petrov) = await world.InstallerAsync("Петров Пётр Петрович");
        var (_, sidorov) = await world.InstallerAsync("Сидоров Семён Семёнович");
        var ids = new List<Guid>();
        foreach (var day in new[] { "2026-11-14", "2026-11-15", "2026-11-16" })
        {
            ids.Add(await CreateAsync(petrov, day, "day"));
            ids.Add(await CreateAsync(sidorov, day, "night"));
        }

        await Task.WhenAll(RunAsync(Up19), RunAsync(Up19));

        foreach (var id in ids)
        {
            Assert.Equal(ReportStatus.Submitted, (await world.ReportAsync(id))!.Status);
            Assert.Single(await EntriesAsync(id), e => e.Kind == ReportJournalKinds.AutoSubmitted);
            Assert.Single(await world.ChangesAsync(id), c => c.Kind == ReportChangeKind.Submitted);
        }
    }

    /// <summary>Возвращённый отчёт с истёкшим окном остаётся возвращённым.</summary>
    [Fact]
    public async Task Возвращённый_не_трогается()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = await CreateAsync(client, "2026-11-16", "day");
        using (var scope = host.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE work.reports SET status = 'returned', submitted_at = {Midnight(17).ToUniversalTime()}, submitted_by = 'manual' WHERE id = {id}");

        await RunAsync(Up19);

        Assert.Equal(ReportStatus.Returned, (await world.ReportAsync(id))!.Status);
        Assert.DoesNotContain(await EntriesAsync(id), e => e.Kind == ReportJournalKinds.AutoSubmitted);
    }

    /// <summary>
    /// Черновик без места у вида «требует места» к концу окна подан, а незаполненное названо в ответе чтения и в журнале
    /// (ТЗ WORK-6, WORK-10).
    /// </summary>
    [Fact]
    public async Task Без_места_к_концу_окна()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var created = await client.PostAsJsonAsync(Url, world.Draft(id, world.SiteA, lineId: line,
            workType: world.WorkTypeNeedsPlace, location: null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        await RunAsync(Up19);

        var body = await ReportWorld.JsonAsync(await client.GetAsync($"{Url}/{id}"));
        Assert.Equal("submitted", body.GetProperty("status").GetString());
        Assert.Equal("auto", body.GetProperty("submittedBy").GetString());
        Assert.Equal(Midnight(17), body.GetProperty("submittedAt").GetDateTimeOffset());
        var missing = Assert.Single(body.GetProperty("requiredMissing").EnumerateArray());
        Assert.Equal(line, missing.GetProperty("lineId").GetGuid());
        Assert.Equal("location", missing.GetProperty("field").GetString());
        var entry = Assert.Single(await EntriesAsync(id), e => e.Kind == ReportJournalKinds.AutoSubmitted);
        Assert.Contains("requiredMissing", entry.Changes);
    }

    /// <summary>Отчёт подан автоподачей в 00:00 17.11, монтажник в 09:00 17.11 правит объём — правка после подачи.</summary>
    [Fact]
    public async Task Правка_после_автоподачи()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Url, world.Draft(id, world.SiteA, lineId: line))).StatusCode);
        await RunAsync(Midnight(17).AddMinutes(3));
        var version = (await ReportWorld.JsonAsync(await client.GetAsync($"{Url}/{id}"))).GetProperty("version").GetString();

        var put = await ReportWorld.PutAsync(client, $"{Url}/{id}", version, world.Draft(id, world.SiteA, lineId: line,
            volume: 150m, changeId: Guid.NewGuid(), deviceTime: "2026-11-17T09:00:00+03:00"));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await EntriesAsync(id)).Last().AfterSubmission);
    }

    /// <summary>Подача, которую обогнала автоподача, — 200 <c>alreadySubmitted</c> с моментом автоподачи.</summary>
    [Fact]
    public async Task Подача_после_автоподачи_уже_подан()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var draft = await ReportWorld.JsonAsync(await client.PostAsJsonAsync(Url, world.Draft(id, world.SiteA)));
        await RunAsync(Up19);

        var late = await SubmissionTests.SubmitAsync(client, $"{Url}/{id}", draft.GetProperty("version").GetString(),
            new DateTimeOffset(2026, 11, 16, 23, 0, 0, TimeSpan.FromHours(3)));

        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        var body = await ReportWorld.JsonAsync(late);
        Assert.True(body.GetProperty("alreadySubmitted").GetBoolean());
        Assert.Equal(Midnight(17), body.GetProperty("submittedAt").GetDateTimeOffset());
        Assert.Equal("auto", body.GetProperty("submittedBy").GetString());
    }

    /// <summary>
    /// Подача одного отчёта падает (здесь — порт видов работ на его виде) — прочие поданы, сбойный остаётся черновиком
    /// до следующего такта: порядок выборки постоянный, и иначе он первым ронял бы каждый прогон.
    /// </summary>
    [Fact]
    public async Task Сбой_одного_отчёта_не_останавливает_прогон()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var broken = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Url, world.Draft(broken, world.SiteA,
            date: "2026-11-15", workType: world.WorkTypeNeedsPlace, location: "Ось 1"))).StatusCode);
        var fine = await CreateAsync(client, "2026-11-16", "day");

        await RunAsync(Up19, types => new FailingOn(types, world.WorkTypeNeedsPlace));

        Assert.Equal(ReportStatus.Draft, (await world.ReportAsync(broken))!.Status);
        Assert.Equal((Midnight(17), ReportSubmitter.Auto), await SubmittedAsync(fine));
        Assert.Equal("подано отчётов: 1", Assert.Single(await CoreRecordsAsync()).After);
    }

    /// <summary>
    /// Справочник видов работ не заведён — отчёт подан концом окна, но «не проверено» не выдано за «всё заполнено»:
    /// запись журнала ядра это называет.
    /// </summary>
    [Fact]
    public async Task Без_справочника_проверка_названа()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = await CreateAsync(client, "2026-11-16", "day");

        await RunAsync(Up19, _ => new NoDirectory());

        Assert.Equal((Midnight(17), ReportSubmitter.Auto), await SubmittedAsync(id));
        Assert.Contains("обязательные поля не проверены у 1", Assert.Single(await CoreRecordsAsync()).After);
    }

    private async Task<Guid> CreateAsync(HttpClient client, string date, string shift)
    {
        var id = Guid.NewGuid();
        var response = await client.PostAsJsonAsync(Url, world.Draft(id, world.SiteA, date: date, shift: shift));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return id;
    }

    /// <summary>
    /// Прогон задачи автоподачи в своей области служб на часах <paramref name="now" />; <paramref name="workTypes" /> —
    /// подмена порта видов работ для проверки обязательных полей.
    /// </summary>
    private async Task RunAsync(DateTimeOffset now, Func<IModuleWorkTypes, IModuleWorkTypes>? workTypes = null)
    {
        using var scope = host.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var extra = new List<object> { (TimeProvider)new Fixed(now) };
        if (workTypes is not null) extra.Add(new RequiredFields(workTypes(sp.GetRequiredService<IModuleWorkTypes>())));
        var job = ActivatorUtilities.CreateInstance<AutoSubmitJob>(sp, [.. extra]);
        await job.RunAsync(new ModuleJobRun(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, null, (_, _, _) => Task.CompletedTask),
            CancellationToken.None);
    }

    private async Task<(DateTimeOffset?, ReportSubmitter?)> SubmittedAsync(Guid id)
    {
        var report = (await world.ReportAsync(id))!;
        Assert.Equal(ReportStatus.Submitted, report.Status);
        return (report.SubmittedAt, report.SubmittedBy);
    }

    private async Task<List<ReportJournalEntry>> EntriesAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Set<ReportJournalEntry>().AsNoTracking()
            .Where(e => e.ReportId == id).OrderBy(e => e.Seq).ToListAsync();
    }

    private async Task<List<BHS.CRG.Domain.Activity.ActivityRecord>> CoreRecordsAsync()
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ActivityRecords.AsNoTracking()
            .Where(r => r.Action == ReportActions.AutoSubmitted.Code).ToListAsync();
    }

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    /// <summary>Порт видов работ, который падает на названном виде, — сбой подачи одного отчёта.</summary>
    private sealed class FailingOn(IModuleWorkTypes inner, Guid failing) : IModuleWorkTypes
    {
        public Task<IReadOnlyList<ModuleWorkType>?> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        public Task<IReadOnlyList<ModuleWorkType>?> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            ids.Contains(failing) ? throw new InvalidOperationException("Порт видов работ упал.") : inner.GetAsync(ids, ct);

        public Task<ModuleChoice<ModuleWorkType>?> SearchAsync(string? query, int limit, CancellationToken ct = default) =>
            inner.SearchAsync(query, limit, ct);

        public Task<IReadOnlyList<ModuleWorkVariant>?> GetVariantsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            inner.GetVariantsAsync(ids, ct);
    }

    /// <summary>Справочник видов работ не заведён.</summary>
    private sealed class NoDirectory : IModuleWorkTypes
    {
        public Task<IReadOnlyList<ModuleWorkType>?> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModuleWorkType>?>(null);

        public Task<IReadOnlyList<ModuleWorkType>?> GetAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModuleWorkType>?>(null);

        public Task<ModuleChoice<ModuleWorkType>?> SearchAsync(string? query, int limit, CancellationToken ct = default) =>
            Task.FromResult<ModuleChoice<ModuleWorkType>?>(null);

        public Task<IReadOnlyList<ModuleWorkVariant>?> GetVariantsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModuleWorkVariant>?>(null);
    }
}
