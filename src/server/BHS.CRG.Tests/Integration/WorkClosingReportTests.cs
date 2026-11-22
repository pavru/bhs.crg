using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Settings;
using Microsoft.Extensions.DependencyInjection;
using static BHS.CRG.Tests.Integration.WorkPeriodWorld;

namespace BHS.CRG.Tests.Integration;

[Collection(AssignmentHost.Collection)]
public class WorkClosingReportTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly WorkPeriodWorld period = new(host);

    public async Task InitializeAsync()
    {
        await period.Review.SeedAsync();
        host.ClosureTime = AssignmentHost.Now;
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingsStore>()
            .SetAsync(AppSettingKeys.CompanyTimeZone, "Asia/Vladivostok");
    }

    public Task DisposeAsync()
    {
        host.ClosureTime = null;
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Ночная_смена_западнее_компании_относится_к_дате_начала()
    {
        var site = await period.World.Assignments.SiteAsync("Западная стройка", "Europe/Moscow");
        var (_, installer) = await period.World.InstallerAsync("Монтажник ночной смены");
        var (_, manager) = await period.World.ManagerAsync("Менеджер западной стройки", site);
        var closer = await period.CloserAsync();
        var id = Guid.NewGuid();
        var created = await installer.PostAsJsonAsync(Reports,
            period.World.Draft(id, site, date: OctoberEnd, shift: "night"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = await ReportWorld.JsonAsync(created);
        Assert.True(draft.GetProperty("autoSubmitAt").GetDateTimeOffset() > AssignmentHost.Now);
        var preview = await PreviewAsync(closer);
        Assert.Contains("поясу стройки", Section(preview).GetProperty("dateRule").GetString());
        Blocked(preview, "submission-window");

        // Окно уже истекло, а такт ещё не обработал черновик.
        host.ClosureTime = draft.GetProperty("autoSubmitAt").GetDateTimeOffset().AddMinutes(1);
        Blocked(await PreviewAsync(closer), "submission-window");

        var acted = new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.FromHours(3));
        var response = await SubmissionTests.SubmitAsync(installer, $"{Reports}/{id}", ReviewWorld.Version(draft), acted);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var submitted = await ReportWorld.JsonAsync(response);
        Assert.Equal(acted, submitted.GetProperty("submittedAt").GetDateTimeOffset());
        Blocked(await PreviewAsync(closer), "pending-review");
        Assert.Equal(HttpStatusCode.OK, (await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(submitted))).StatusCode);

        var clear = await PreviewAsync(closer);
        Assert.True(clear.GetProperty("canClose").GetBoolean());
        Assert.Equal(1, Assert.Single(Section(clear).GetProperty("frozen").EnumerateArray()).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Возврат_блокирует_отдельно_до_повторной_подачи_и_приёмки()
    {
        var site = period.World.SiteA;
        var (_, installer) = await period.World.InstallerAsync("Монтажник");
        var (_, manager) = await period.World.ManagerAsync("Менеджер", site);
        var closer = await period.CloserAsync();
        var id = Guid.NewGuid();
        var submitted = await period.Review.SubmittedAsync(installer, id, site, OctoberEnd);
        var pending = await PreviewAsync(closer);
        Blocked(pending, "pending-review");
        var returned = await ReviewWorld.ReturnAsync(manager, id, ReviewWorld.Version(submitted), "Уточнить объём");
        Assert.Equal(HttpStatusCode.OK, returned.StatusCode);

        var preview = await PreviewAsync(closer);
        Blocked(preview, "returned");
        Assert.NotEqual(Stamp(pending), Stamp(preview));
        var refused = await SendAsync(closer, OctoberEnd, null, "2026-10-01", "none", Stamp(preview));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Возвращены на правку: 1 отчёт", await AssignmentWorld.ErrorAsync(refused));
        Assert.Empty((await closer.GetFromJsonAsync<JsonElement>(PeriodPath + "/history")).EnumerateArray());
        using (var scope = host.Services.CreateScope())
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IActivityLog>()
                .CountAsync(ActivityVisibility.Whole, ActivityActions.PeriodClosed.Code));

        var again = await SubmissionTests.SubmitAsync(installer, $"{Reports}/{id}",
            ReviewWorld.Version(await ReportWorld.JsonAsync(returned)), AssignmentHost.Now);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Blocked(await PreviewAsync(closer), "pending-review");
        Assert.Equal(HttpStatusCode.OK,
            (await ReviewWorld.AcceptAsync(manager, id, ReviewWorld.Version(await ReportWorld.JsonAsync(again)))).StatusCode);
        await CloseAsync(closer);
    }

    [Fact]
    public async Task Компания_не_считает_повторно_дни_закрытые_стройкой()
    {
        var site = period.World.SiteA;
        var (_, installer) = await period.World.InstallerAsync("Монтажник стройки");
        var (_, manager) = await period.World.ManagerAsync("Менеджер стройки", site);
        var closer = await period.CloserAsync();
        await period.AcceptedAsync(installer, manager, Guid.NewGuid(), site);

        await CloseAsync(closer, site: site);
        var company = Section(await PreviewAsync(closer));
        Assert.Empty(company.GetProperty("frozen").EnumerateArray());
        Assert.Empty(company.GetProperty("blocking").EnumerateArray());
    }

    private static void Blocked(JsonElement preview, string code)
    {
        Assert.False(preview.GetProperty("canClose").GetBoolean());
        var row = Assert.Single(Section(preview).GetProperty("blocking").EnumerateArray());
        Assert.Equal(code, row.GetProperty("key").GetString());
        Assert.Equal(1, row.GetProperty("count").GetInt32());
    }
}
