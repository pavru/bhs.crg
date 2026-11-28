using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Modules.Work;
using BHS.CRG.Tests.Support;
using static BHS.CRG.Tests.Integration.FieldReportClient;
using static BHS.CRG.Tests.Integration.ReviewWorld;

namespace BHS.CRG.Tests.Integration;

public sealed class FieldDeviceProxyHost : AssignmentHost
{
    protected override string DatabaseSuffix => "field_proxy";
    protected override bool UseProductionRateLimits => true;

    public FieldDeviceProxyHost() => UseKestrel(options => options.ListenAnyIP(0));

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.BaseAddress = new UriBuilder(client.BaseAddress!) { Host = "127.0.0.1" }.Uri;
    }
}

public sealed class FieldDeviceProxyFactAttribute : FactAttribute
{
    public FieldDeviceProxyFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BHS_TEST_FIELD_PROXY") != "1")
            Skip = "Запускается с BHS_TEST_FIELD_PROXY=1, Docker и Python.";
    }
}

public sealed class FieldDeviceProxyTests(FieldDeviceProxyHost host) : IClassFixture<FieldDeviceProxyHost>
{
    [FieldDeviceProxyFact]
    public async Task Session_can_join_and_refresh_through_the_external_proxy()
    {
        var world = new AssignmentWorld(host);
        await world.ResetAsync();
        var employee = await world.EmployeeAsync(await world.DirectoryAsync(), "Монтажник без офисной записи");
        var (_, manager) = await world.UserAsync("Администратор устройств", WorkModule.DevicesManage, WorkModule.SitesAll);
        using var office = manager;
        using var invite = await office.PostAsJsonAsync($"/api/work/devices/employees/{employee}/invitations",
            new { serverUrl = "https://field.example.test", transport = "https-system" });
        invite.EnsureSuccessStatusCode();
        var invitationCode = (await AssignmentWorld.JsonAsync(invite)).GetProperty("invitationCode").GetString();

        var start = new ProcessStartInfo("python")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(RepoPaths.File("deploy", "field-proxy.tests.py"));
        start.ArgumentList.Add(office.BaseAddress!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--invitation-stdin");
        using var probe = Process.Start(start)!;
        var stdout = probe.StandardOutput.ReadToEndAsync();
        var stderr = probe.StandardError.ReadToEndAsync();
        await probe.StandardInput.WriteAsync(JsonSerializer.Serialize(new { invitationCode, employeeId = employee }));
        probe.StandardInput.Close();
        await probe.WaitForExitAsync();
        Assert.True(probe.ExitCode == 0, (await stdout) + (await stderr));
    }

    [FieldDeviceProxyFact]
    public async Task Field_report_exchange_through_external_nginx_enforces_owner_and_all_revocations()
    {
        var review = new ReviewWorld(host);
        await review.SeedAsync();
        var world = review.Reports;
        var employee = await world.EmployeeAsync("Монтажник с телефоном");
        var otherEmployee = await world.EmployeeAsync("Другой монтажник");
        var (_, manager) = await world.Assignments.UserAsync("Менеджер", WorkModule.ReportReview,
            WorkModule.DevicesManage, WorkModule.SitesAll);
        using var office = manager;
        var start = new ProcessStartInfo("python")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(RepoPaths.File("deploy", "field-proxy.tests.py"));
        start.ArgumentList.Add(office.BaseAddress!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--hold-stdin");
        using var proxy = Process.Start(start)!;
        var errors = proxy.StandardError.ReadToEndAsync();
        try
        {
            var address = await proxy.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(address is not null, await ErrorIfExitedAsync(proxy, errors));
            var uri = new Uri(address!);
            using var otherPhone = new HttpClient { BaseAddress = uri };
            await ConnectAsync(office, otherPhone, otherEmployee);
            var other = new FieldReportClient(otherPhone);
            var foreign = Guid.NewGuid();
            using var foreignCreated = await other.CreateAsync(Draft(foreign, world.SiteA, "2026-10-31"));
            var foreignReport = await ReadAsync(foreignCreated, HttpStatusCode.Created);
            var foreignVersion = Version(foreignReport);

            using var activePhone = new HttpClient { BaseAddress = uri };
            await ConnectAsync(office, activePhone, employee);
            var active = new FieldReportClient(activePhone);
            var own = Guid.NewGuid();
            using var activeWritten = await active.CreateAsync(Draft(own, world.SiteA, "2026-11-01"));
            var ownBody = await ReadAsync(activeWritten, HttpStatusCode.Created);
            var ownJournal = await JournalAsync(own);
            using var activeRead = await active.GetAsync(own);
            Assert.Equal(Version(ownBody), Version(await ReadAsync(activeRead, HttpStatusCode.OK)));
            Assert.Equal(own, Assert.Single((await active.FeedAsync()).GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
            var priorJournal = await JournalAsync(foreign);
            await RefusedAsync(active.GetAsync(foreign), HttpStatusCode.NotFound, "report-not-found");
            await RefusedAsync(active.ChangeAsync(foreign, foreignVersion, Draft(foreign, world.SiteA, "2026-10-31")),
                HttpStatusCode.NotFound, "report-not-found");
            await RefusedAsync(active.SubmitAsync(foreign, foreignVersion, Guid.NewGuid()), HttpStatusCode.NotFound, "report-not-found");
            await UnchangedAsync(other, foreign, foreignReport, priorJournal);

            foreach (var cause in new[] { "replacement", "dismissal" })
            {
                host.ModuleTime = AssignmentHost.Now;
                var site = cause == "replacement" ? world.SiteA : world.SiteB;
                using var phone = new HttpClient { BaseAddress = uri };
                var credentials = await ConnectAsync(office, phone, employee);
                await RevokeAsync(office, credentials, cause);
                var revoked = new FieldReportClient(phone);
                await RefusedAsync(revoked.GetAsync(own), HttpStatusCode.Forbidden, "device-revoked");
                var days = new[] { "2026-10-30", "2026-10-31" };
                foreach (var day in days)
                {
                    var id = Guid.NewGuid();
                    using var wrote = await revoked.CreateAsync(Draft(id, site, day));
                    var saved = await ReadAsync(wrote, HttpStatusCode.Created);
                    using var submitted = await revoked.SubmitAsync(id, Version(saved), Guid.NewGuid());
                    var report = await ReadAsync(submitted, HttpStatusCode.OK);
                    Assert.Equal("submitted", report.GetProperty("status").GetString());
                    Assert.Equal(2, (await review.EntriesAsync(id)).Count);
                }

                await RefusedAsync(revoked.ChangeAsync(foreign, foreignVersion, Draft(foreign, site, "2026-10-31")),
                    HttpStatusCode.NotFound, "report-not-found");
                await RefusedAsync(revoked.SubmitAsync(foreign, foreignVersion, Guid.NewGuid()), HttpStatusCode.NotFound,
                    "report-not-found");
                await RefusedAsync(revoked.ChangeAsync(own, Version(ownBody), Draft(own, world.SiteA, "2026-10-30")),
                    HttpStatusCode.Forbidden, "device-revoked");
                await RefusedAsync(revoked.SubmitAsync(own, Version(ownBody), Guid.NewGuid()),
                    HttpStatusCode.Forbidden, "device-revoked");
                await RefuseNewAsync(revoked, site, "2026-11-02");
                await UnchangedAsync(other, foreign, foreignReport, priorJournal);
                await UnchangedAsync(active, own, ownBody, ownJournal);

                host.ModuleTime = AssignmentHost.Now.AddDays(14);
                await RefuseNewAsync(revoked, site, "2026-10-29");
            }

            host.ModuleTime = AssignmentHost.Now;
            foreach (var previouslyRevoked in new[] { false, true })
            {
                using var phone = new HttpClient { BaseAddress = uri };
                var initial = await ConnectAsync(office, phone, employee);
                var next = await RotateAsync(phone, initial);
                if (previouslyRevoked) await RevokeAsync(office, next, "replacement");
                await RevokeAsync(office, next, "lost");
                var lost = new FieldReportClient(phone);
                await RefuseNewAsync(lost, world.SiteB, "2026-10-28");
                await RefusedAsync(lost.GetAsync(own), HttpStatusCode.Forbidden, "device-revoked");
                await RefusedAsync(lost.SubmitAsync(own, Version(ownBody), Guid.NewGuid()),
                    HttpStatusCode.Forbidden, "device-revoked");
                foreach (var refresh in new[] { initial.RefreshToken, next.RefreshToken })
                    await RefusedAsync(RefreshAsync(phone, refresh), HttpStatusCode.Forbidden, "device-revoked");
            }
            Assert.Equal(6, await world.ReportCountAsync());
            await UnchangedAsync(active, own, ownBody, ownJournal);
            await UnchangedAsync(other, foreign, foreignReport, priorJournal);
        }
        finally
        {
            host.ModuleTime = null;
            proxy.StandardInput.Close();
            await proxy.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }

        object Draft(Guid id, Guid site, string day) => world.Draft(id, site, date: day, changeId: Guid.NewGuid());

        async Task<string> JournalAsync(Guid id) => JsonSerializer.Serialize(await review.EntriesAsync(id));

        async Task UnchangedAsync(FieldReportClient client, Guid id, JsonElement before, string journal)
        {
            using var read = await client.GetAsync(id);
            var current = await ReadAsync(read, HttpStatusCode.OK);
            Assert.True(JsonElement.DeepEquals(before, current), $"Отчёт изменился. До: {before}. После: {current}.");
            Assert.Equal(journal, await JournalAsync(id));
        }

        async Task RefuseNewAsync(FieldReportClient client, Guid site, string day)
        {
            var id = Guid.NewGuid();
            await RefusedAsync(client.CreateAsync(Draft(id, site, day)), HttpStatusCode.Forbidden, "device-revoked");
            Assert.Null(await world.ReportAsync(id));
            Assert.Empty(await review.EntriesAsync(id));
            Assert.Empty(await world.ChangesAsync(id));
        }
    }

    private static async Task<string> ErrorIfExitedAsync(Process process, Task<string> errors) =>
        process.HasExited ? await errors : "Внешний nginx не опубликовал адрес.";
}
