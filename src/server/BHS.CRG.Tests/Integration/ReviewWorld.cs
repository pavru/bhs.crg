using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Modules.Work.Review;
using BHS.CRG.Modules.Work.Submission;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Мир приёмки (change <c>work-16-review</c>): поверх мира отчётов — поданный отчёт, приёмка и возврат адресами, журнал,
/// колокольчик и прогон такта автоподачи на подставных часах. Сотрудники — вымышленные.
/// </summary>
internal sealed class ReviewWorld(AssignmentHost host)
{
    /// <summary>18:00 16.11 по Москве — подача дневного отчёта за 16.11 до конца окна.</summary>
    public static readonly DateTimeOffset Evening16 = new(2026, 11, 16, 18, 0, 0, TimeSpan.FromHours(3));

    /// <summary>Полдень 19.11 по Москве — окна отчётов за 16.11 давно закрылись.</summary>
    public static readonly DateTimeOffset Up19 = new(2026, 11, 19, 12, 0, 0, TimeSpan.FromHours(3));

    public ReportWorld Reports { get; } = new(host);

    public Guid SiteA => Reports.SiteA;

    public Guid SiteB => Reports.SiteB;

    public Task SeedAsync() => Reports.SeedAsync();

    /// <summary>Свой отчёт монтажника, поданный в 18:00 16.11 (или за <paramref name="date" />); ответ подачи.</summary>
    public async Task<JsonElement> SubmittedAsync(HttpClient installer, Guid id, Guid site, string date = ReportWorld.Day16,
        Guid? lineId = null)
    {
        var created = await installer.PostAsJsonAsync(ReportEndpoints.Path, Reports.Draft(id, site, date: date, lineId: lineId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await ReportWorld.JsonAsync(created);
        var submitted = await SubmissionTests.SubmitAsync(installer, $"{ReportEndpoints.Path}/{id}", Version(draft), Evening16);
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        return await ReportWorld.JsonAsync(submitted);
    }

    public static string? Version(JsonElement report) => report.GetProperty("version").GetString();

    /// <summary>Отчёт глазами менеджера — адресом ввода за сотрудника.</summary>
    public static async Task<JsonElement> AsManagerAsync(HttpClient manager, Guid id)
    {
        var response = await manager.GetAsync($"{EntryReportEndpoints.Path}/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReportWorld.JsonAsync(response);
    }

    public static Task<HttpResponseMessage> AcceptAsync(HttpClient client, Guid id, string? version) =>
        client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Post, $"{ReportEndpoints.Path}/{id}/accept", version,
            new { changeId = Guid.NewGuid() }));

    /// <summary>Решение адресом <c>…/accept</c> или <c>…/return</c> с телом как есть — для повтора по <c>changeId</c>.</summary>
    public static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid id, string action, string? version, object body) =>
        client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Post, $"{ReportEndpoints.Path}/{id}/{action}", version, body));

    public static Task<HttpResponseMessage> ReturnAsync(HttpClient client, Guid id, string? version, string? comment) =>
        client.SendAsync(AssignmentWorld.Versioned(HttpMethod.Post, $"{ReportEndpoints.Path}/{id}/return", version,
            new { comment, changeId = Guid.NewGuid() }));

    public async Task<List<ReportJournalEntry>> EntriesAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Set<ReportJournalEntry>().AsNoTracking()
            .Where(e => e.ReportId == id).OrderBy(e => e.Seq).ToListAsync();
    }

    /// <summary>Записи журнала действий ядра с этим кодом по этому отчёту.</summary>
    public async Task<List<BHS.CRG.Domain.Activity.ActivityRecord>> CoreRecordsAsync(string action, Guid reportId)
    {
        using var scope = host.Services.CreateScope();
        var target = reportId.ToString();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ActivityRecords.AsNoTracking()
            .Where(r => r.Action == action && r.TargetId == target).ToListAsync();
    }

    /// <summary>Колокольчик учётной записи: заголовок и текст каждого уведомления.</summary>
    public static async Task<List<(string Title, string Message)>> BellAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/notifications");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReportWorld.JsonAsync(response);
        return
        [
            .. body.GetProperty("items").EnumerateArray()
                .Select(i => (i.GetProperty("title").GetString()!, i.GetProperty("message").GetString()!)),
        ];
    }

    /// <summary>Прогон задачи такта автоподачи — подача и сводка — на часах <paramref name="now" />.</summary>
    public async Task TickAsync(DateTimeOffset now)
    {
        using var scope = host.Services.CreateScope();
        var job = ActivatorUtilities.CreateInstance<AutoSubmitJob>(scope.ServiceProvider, (TimeProvider)new Fixed(now));
        await job.RunAsync(new ModuleJobRun(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, null, (_, _, _) => Task.CompletedTask),
            CancellationToken.None);
    }

    /// <summary>
    /// Шаг сводки на часах <paramref name="now" />, где публикация учётной записи, для которой <paramref name="fails" />
    /// отвечает «да», падает.
    /// </summary>
    public async Task DigestAsync(DateTimeOffset now, Func<Guid?, bool> fails)
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var notifications = new Failing(services.GetRequiredService<IModuleNotifications>(), fails);
        var digest = ActivatorUtilities.CreateInstance<SubmittedDigest>(services, (IModuleNotifications)notifications);
        await digest.RunAsync(services.GetRequiredService<IModuleReaders>().ForSystem("тест сводки"), now, CancellationToken.None);
    }

    /// <summary>Отметки сводки отчёта.</summary>
    public async Task<int> DigestMarksAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Set<ReviewDigestMark>()
            .CountAsync(m => m.ReportId == id);
    }

    private sealed class Failing(IModuleNotifications inner, Func<Guid?, bool> fails) : IModuleNotifications
    {
        public Task<Guid> PublishAsync(ModuleNotificationLevel level, string title, string message, string? audience = null,
            Guid? userId = null, string? linkUrl = null, string? linkLabel = null, CancellationToken ct = default) =>
            fails(userId)
                ? throw new InvalidOperationException("колокольчик недоступен")
                : inner.PublishAsync(level, title, message, audience, userId, linkUrl, linkLabel, ct);
    }

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
