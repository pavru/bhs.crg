using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Work;

[Collection(AssignmentHost.Collection)]
public class CrewBackupTests(AssignmentHost host) : IAsyncLifetime
{
    private readonly CrewWorld crew = new(host);

    public Task InitializeAsync() => crew.SeedAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restoring_a_draft_over_a_submitted_report_invalidates_the_existing_total(bool legacy)
    {
        var (_, installer) = await crew.Reports.InstallerAsync("Монтажник");
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var report = Guid.NewGuid();
        var draft = await CrewWorld.CreateAsync(installer, crew.Body(report, Guid.NewGuid(), 120));
        var backup = await ReadBackupAsync();
        if (legacy) backup = AsLegacy(backup);
        await CrewWorld.SubmitAsync(installer, report, draft);
        var pending = Assert.Single(await crew.GroupsAsync(manager));
        var id = pending.GetProperty("id").GetGuid();
        var approved = await CrewWorld.JsonAsync(await CrewWorld.ApproveAsync(manager, id, ReviewWorld.Version(pending)));

        await RestoreAsync(backup);

        var restored = Assert.Single(await crew.GroupsAsync(manager));
        Assert.Equal(id, restored.GetProperty("id").GetGuid());
        Assert.Equal("stale", restored.GetProperty("state").GetString());
        Assert.Equal(120m, restored.GetProperty("total").GetDecimal());
        Assert.Empty(restored.GetProperty("lines").EnumerateArray());
        Assert.NotEqual(ReviewWorld.Version(approved), ReviewWorld.Version(restored));
    }

    [Fact]
    public async Task Restoring_an_approved_group_keeps_the_total_from_the_same_backup()
    {
        var (_, installer) = await crew.Reports.InstallerAsync("Монтажник");
        var (_, secondInstaller) = await crew.Reports.InstallerAsync("Второй монтажник");
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await crew.SubmittedAsync(installer, first, Guid.NewGuid(), 120);
        var draft = await CrewWorld.CreateAsync(secondInstaller, crew.Body(second, Guid.NewGuid(), 140));
        var pending = Assert.Single(await crew.GroupsAsync(manager));
        var id = pending.GetProperty("id").GetGuid();
        await CrewWorld.JsonAsync(await CrewWorld.ApproveAsync(manager, id, ReviewWorld.Version(pending)));
        var backup = await ReadBackupAsync();

        await CrewWorld.SubmitAsync(secondInstaller, second, draft);
        var stale = Assert.Single(await crew.GroupsAsync(manager));
        await CrewWorld.JsonAsync(await CrewWorld.ApproveAsync(manager, id, ReviewWorld.Version(stale), total: 140));
        await RestoreAsync(backup);

        var restored = Assert.Single(await crew.GroupsAsync(manager));
        Assert.Equal("approved", restored.GetProperty("state").GetString());
        Assert.Equal(120m, restored.GetProperty("total").GetDecimal());
        var line = Assert.Single(restored.GetProperty("lines").EnumerateArray());
        Assert.Equal(first, line.GetProperty("reportId").GetGuid());
    }

    [Fact]
    public async Task Restoring_the_old_shift_invalidates_the_group_the_report_leaves()
    {
        var (_, installer) = await crew.Reports.InstallerAsync("Монтажник");
        var (_, manager) = await crew.Reports.ManagerAsync("Менеджер", crew.Site, WorkModule.CrewApprove);
        var report = Guid.NewGuid();
        var line = Guid.NewGuid();
        var night = await crew.SubmittedAsync(installer, report, line, 120, shift: "night");
        var backup = AsLegacy(await ReadBackupAsync());

        var returned = await CrewWorld.JsonAsync(await ReviewWorld.ReturnAsync(manager, report, ReviewWorld.Version(night),
            "Смену указали неверно"));
        var day = await CrewWorld.JsonAsync(await ReportWorld.PutAsync(installer, $"{ReportEndpoints.Path}/{report}",
            ReviewWorld.Version(returned), crew.Body(report, line, 120)));
        await CrewWorld.SubmitAsync(installer, report, day);
        var pending = Assert.Single(await crew.GroupsAsync(manager), g => g.GetProperty("shift").GetString() == "day");
        await CrewWorld.JsonAsync(await CrewWorld.ApproveAsync(manager, pending.GetProperty("id").GetGuid(), ReviewWorld.Version(pending)));

        await RestoreAsync(backup);

        var groups = await crew.GroupsAsync(manager);
        var oldDay = Assert.Single(groups, g => g.GetProperty("shift").GetString() == "day");
        Assert.Equal("stale", oldDay.GetProperty("state").GetString());
        Assert.Empty(oldDay.GetProperty("lines").EnumerateArray());
        var restoredNight = Assert.Single(groups, g => g.GetProperty("shift").GetString() == "night");
        Assert.Equal(report, Assert.Single(restoredNight.GetProperty("lines").EnumerateArray()).GetProperty("reportId").GetGuid());
    }

    private async Task<BackupModuleSchema> ReadBackupAsync()
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        var backup = services.GetRequiredService<IModuleSchemaBackup>();
        return Assert.Single(await backup.ReadAsync(tx.GetDbTransaction(), [], default));
    }

    private async Task RestoreAsync(BackupModuleSchema data)
    {
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        var backup = services.GetRequiredService<IModuleSchemaBackup>();
        var restored = await backup.RestoreAsync([data], tx.GetDbTransaction(), [], default);
        Assert.Single(restored.Restored);
        await tx.CommitAsync();
    }

    private static BackupModuleSchema AsLegacy(BackupModuleSchema data) =>
        data with
        {
            Tables = [.. data.Tables.Where(t => t.Table != "crew_groups").Select(t => t.Table == "report_lines"
                ? t with { Rows = [.. t.Rows.Select(RemoveCrewRole)] }
                : t)],
        };

    private static JsonElement RemoveCrewRole(JsonElement row)
    {
        var values = JsonNode.Parse(row.GetRawText())!.AsObject();
        values.Remove("crew_role");
        return JsonSerializer.SerializeToElement(values);
    }
}
