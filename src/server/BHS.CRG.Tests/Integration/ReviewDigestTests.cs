using System.Net;
using System.Net.Http.Json;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Сводка о поданных отчётах (change <c>work-16-review</c>; ТЗ WORK-56, WORK-41, D16 зонтика): личная каждому менеджеру
/// стройки за такт автоподачи, по закрывшемуся окну, одна на два прогона, без держателей «на любых стройках» и без
/// сдвига версии отчёта. Такт — прямым вызовом задачи на подставных часах. Сотрудники — вымышленные.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class ReviewDigestTests(AssignmentHost host) : IAsyncLifetime
{
    private const string Url = ReportEndpoints.Path;
    private const string Digest = "Подано отчётов";
    private readonly ReviewWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Две стройки — две разные сводки: менеджер стройки А видит только свою, с поданным вручную и автоподанным
    /// отчётами; менеджер Б — только свою. Держатель <c>work.sites.all</c> без назначения не получает ничего.
    /// </summary>
    [Fact]
    public async Task Две_стройки_две_сводки()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, sidorov) = await world.Reports.InstallerAsync("Сидоров Семён Семёнович");
        var (_, ivanova) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (_, kuznetsov) = await world.Reports.ManagerAsync("Кузнецов Кирилл Константинович", world.SiteB);
        var admin = await AdminAsync();
        await world.SubmittedAsync(petrov, Guid.NewGuid(), world.SiteA);
        Assert.Equal(HttpStatusCode.Created, (await petrov.PostAsJsonAsync(Url,
            world.Reports.Draft(Guid.NewGuid(), world.SiteA, date: "2026-11-15"))).StatusCode);
        await world.SubmittedAsync(sidorov, Guid.NewGuid(), world.SiteB);

        await world.TickAsync(ReviewWorld.Up19);

        var a = Assert.Single(await ReviewWorld.BellAsync(ivanova));
        Assert.Equal($"{Digest}: 2", a.Title);
        Assert.Equal("Ждут приёмки: Стройка А — 2", a.Message);
        var b = Assert.Single(await ReviewWorld.BellAsync(kuznetsov));
        Assert.Equal($"{Digest}: 1", b.Title);
        Assert.DoesNotContain("Стройка А", b.Message);
        Assert.Empty(await ReviewWorld.BellAsync(admin));
        Assert.Empty(await ReviewWorld.BellAsync(petrov));
    }

    /// <summary>Два прогона подряд и два разом — у менеджера одна сводка.</summary>
    [Fact]
    public async Task Два_прогона_одна_сводка()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, ivanova) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        await world.SubmittedAsync(petrov, Guid.NewGuid(), world.SiteA);
        await world.SubmittedAsync(petrov, Guid.NewGuid(), world.SiteA, date: "2026-11-15");

        await Task.WhenAll(world.TickAsync(ReviewWorld.Up19), world.TickAsync(ReviewWorld.Up19));
        await world.TickAsync(ReviewWorld.Up19.AddMinutes(5));

        Assert.Equal($"{Digest}: 2", Assert.Single(await ReviewWorld.BellAsync(ivanova)).Title);
    }

    /// <summary>
    /// Ручная подача в 18:00 ждёт полуночи стройки: такт в 19:00 сводки не шлёт, после полуночи — шлёт. Отметка сводки
    /// версии отчёта не сдвигает: правка по версии, прочитанной до сводки, проходит.
    /// </summary>
    [Fact]
    public async Task Ручная_подача_ждёт_окна_и_версия_прежняя()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, ivanova) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var submitted = await world.SubmittedAsync(petrov, id, world.SiteA, lineId: line);

        await world.TickAsync(ReviewWorld.Evening16.AddHours(1));
        Assert.Empty(await ReviewWorld.BellAsync(ivanova));
        Assert.Equal(0, await world.DigestMarksAsync(id));

        await world.TickAsync(ReviewWorld.Evening16.AddHours(6).AddMinutes(5));
        Assert.Single(await ReviewWorld.BellAsync(ivanova));
        Assert.Equal(1, await world.DigestMarksAsync(id));

        var after = await ReportWorld.JsonAsync(await petrov.GetAsync($"{Url}/{id}"));
        Assert.Equal(ReviewWorld.Version(submitted), ReviewWorld.Version(after));
        var put = await ReportWorld.PutAsync(petrov, $"{Url}/{id}", ReviewWorld.Version(submitted),
            world.Reports.Draft(id, world.SiteA, lineId: line, volume: 150m, changeId: Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
    }

    /// <summary>Возвращённый и снова поданный отчёт — новый момент подачи, и сводка о нём приходит снова.</summary>
    [Fact]
    public async Task Повторная_подача_снова_в_сводке()
    {
        var (_, petrov) = await world.Reports.InstallerAsync("Петров Пётр Петрович");
        var (_, ivanova) = await world.Reports.ManagerAsync("Иванова Ирина Ивановна", world.SiteA);
        var id = Guid.NewGuid();
        await world.SubmittedAsync(petrov, id, world.SiteA);
        await world.TickAsync(ReviewWorld.Up19);
        var seen = ReviewWorld.Version(await ReviewWorld.AsManagerAsync(ivanova, id));
        var returned = await ReportWorld.JsonAsync(await ReviewWorld.ReturnAsync(ivanova, id, seen, "Объём"));
        var again = await SubmissionTests.SubmitAsync(petrov, $"{Url}/{id}", ReviewWorld.Version(returned), DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // Такт — после закрытия окна (часы подачи в этом хосте настоящие, а такта — подставные).
        await world.TickAsync(ReviewWorld.Up19.AddMinutes(5));

        Assert.Equal(2, (await ReviewWorld.BellAsync(ivanova)).Count(n => n.Title.StartsWith(Digest)));
        Assert.Equal(2, await world.DigestMarksAsync(id));
    }

    private async Task<HttpClient> AdminAsync()
    {
        var employee = await world.Reports.EmployeeAsync("Орлова Ольга Олеговна");
        var (userId, client) = await world.Reports.Assignments.UserAsync("Орлова Ольга Олеговна", WorkModule.ReportReview,
            WorkModule.SitesAll);
        await world.Reports.Assignments.LinkAsync(employee, userId);
        return client;
    }
}
