using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Api.Auth;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Tests.Work;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Sdk;

namespace BHS.CRG.Tests.Integration;

internal sealed class WorkIsolationControlHost() : WorkIsolationHost(new WorkIsolationReadControl())
{
    protected override string DatabaseSuffix => "workisocontrol";
}

public sealed partial class WorkIsolationViolationTests
{
    [Fact]
    public async Task Отчёт_виден_только_в_охвате_читающей_роли()
    {
        await using var control = new WorkIsolationControlHost();
        await control.InitializeAsync();
        var regular = new WorkIsolationScenario(testHost);
        var comparison = new WorkIsolationScenario(control);

        Assert.NotEqual(testHost.ConnectionString, control.ConnectionString);
        using var regularSeed = await regular.PrepareAsync();
        using var controlSeed = await comparison.PrepareAsync();
        Assert.NotSame(testHost.Services.GetRequiredService<FakeBlobStorage>(),
            control.Services.GetRequiredService<FakeBlobStorage>());
        Assert.NotEqual(regularSeed.ReportA, controlSeed.ReportA);
        Assert.NotEqual(regularSeed.ReportB, controlSeed.ReportB);
        Assert.Equal(regularSeed.Sources.Select(source => source.Key), controlSeed.Sources.Select(source => source.Key));
        Assert.Empty(regularSeed.Sources.Select(source => source.SourceId).Intersect(controlSeed.Sources.Select(source => source.SourceId)));
        Assert.Equal(regularSeed.Photo.Original, controlSeed.Photo.Original);
        Assert.Equal(regularSeed.Photo.Preview, controlSeed.Photo.Preview);

        await regular.CheckPrimaryBoundaryAsync(regularSeed);
        await regular.CheckIsolationAsync(regularSeed, expanded: false);
        await comparison.CheckIsolationAsync(controlSeed, expanded: true);
    }
}

internal sealed partial class WorkIsolationScenario
{
    internal sealed record PublishedSource(string Table, string Key, Guid FileId, Guid SourceId);

    internal sealed record SeedCheck(Guid ReportA, Guid ReportB, IsolationReaders Readers, PublishedSource[] Sources,
        (byte[] Original, byte[] Preview) Photo) : IDisposable
    {
        public void Dispose() => Readers.Dispose();
    }

    internal async Task<SeedCheck> PrepareAsync()
    {
        var readers = await SeedAsync();
        try
        {
            await CheckReportAsync(readers.Installer, reportA, lineA, HoursA, VolumeA, "Участок А");
            await CheckReportAsync(readers.Owner, reportB, lineB, HoursB, VolumeB, marker);
            var sources = await PublishSourcesAsync();
            foreach (var source in sources)
            {
                var mcp = await McpAsync(readers.Executive, "get_rows", new { sourceId = source.SourceId },
                    "totalRows", canReadReport: true);
                Assert.Equal(2, mcp.GetProperty("totalRows").GetInt32());
            }
            return new(reportA, reportB, readers, sources, await ReadStoredPhotoAsync());
        }
        catch
        {
            readers.Dispose();
            throw;
        }
    }

    private async Task<PublishedSource[]> PublishSourcesAsync()
    {
        var (_, publisher) = await world.Assignments.UserAsync("Публикатор", WorkModule.ReportRead, CorePermissions.DataSetsEdit);
        using var publish = publisher;
        var reports = await PublishDataSetAsync(publisher, WorkTableWorld.Reports, WorkTableWorld.ReportsMarker);
        var lines = await PublishDataSetAsync(publisher, WorkTableWorld.Lines, WorkTableWorld.LinesMarker);
        return [new(WorkTableWorld.Reports, WorkTableWorld.ReportsMarker, reports.FileId, reports.SourceId),
            new(WorkTableWorld.Lines, WorkTableWorld.LinesMarker, lines.FileId, lines.SourceId)];
    }

    internal async Task CheckPrimaryBoundaryAsync(SeedCheck seed)
    {
        var control = testHost.ReadControl;
        control?.Start(installerAccount, unassignedAccount, world.SiteA, world.SiteB);
        try
        {
            await AssertReportBExcludedAsync(seed.Readers.Installer);
        }
        finally
        {
            control?.Stop();
        }
    }

    internal async Task CheckIsolationAsync(SeedCheck seed, bool expanded)
    {
        var control = expanded ? testHost.ReadControl ?? throw new InvalidOperationException("Нет контрольных служб чтения.") : null;
        if (expanded) await CheckProbeFailuresAsync(seed.Readers.Installer);
        using var core = await PrepareCoreAsync();
        control?.Start(installerAccount, unassignedAccount, world.SiteA, world.SiteB);
        try
        {
            var readers = seed.Readers;
            var catalog = new ProbeCatalog();
            var work = RegisterWorkRoutes(catalog, readers, seed.Photo, expanded);
            var snapshots = SnapshotReaders(readers.Installer, readers.Unassigned, readers.Executive, expanded);
            RegisterTableProbes(catalog, snapshots);
            RegisterDataSetProbes(catalog, snapshots, seed.Sources);
            RegisterCoreProbes(catalog, readers, core);
            await catalog.RunAsync([.. work, .. TableProbeNames(), .. DataSetProbeNames(), .. CoreProbeNames]);
            if (expanded)
            {
                var failure = await Assert.ThrowsAsync<FalseException>(() => AssertReportBExcludedAsync(readers.Installer));
                Assert.Contains(ForeignReportKey, failure.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            control?.Stop();
        }
        await CheckCombinedRoleAsync();
    }

    private static async Task CheckReportAsync(HttpClient client, Guid id, Guid line, decimal hours, decimal volume, string location) =>
        CheckReport(await client.GetFromJsonAsync<JsonElement>($"{ReportEndpoints.Path}/{id}"), id, line, hours, volume, location);

    private static void CheckReport(JsonElement report, Guid id, Guid line, decimal hours, decimal volume, string location)
    {
        Assert.Equal(id, report.GetProperty("id").GetGuid());
        Assert.Equal("submitted", report.GetProperty("status").GetString());
        Assert.Equal(hours, report.GetProperty("hours").GetDecimal());
        var row = Assert.Single(report.GetProperty("lines").EnumerateArray());
        Assert.Equal(line, row.GetProperty("id").GetGuid());
        Assert.Equal(volume, row.GetProperty("volume").GetDecimal());
        Assert.Equal(location, row.GetProperty("location").GetString());
    }

    private void CheckOwnFeed(JsonElement feed, bool includesA, bool includesB)
    {
        var rows = Items(feed).ToArray();
        Guid[] expected = (includesA, includesB) switch
        {
            (true, true) => [reportA, reportB],
            (true, false) => [reportA],
            _ => [reportB],
        };
        Assert.Equal(expected, rows.Select(row => row.GetProperty("id").GetGuid()));
        if (includesB) Assert.Equal(marker, rows[^1].GetProperty("note").GetString());
        Assert.Empty(feed.GetProperty("deleted").EnumerateArray());
        Assert.True(feed.GetProperty("cursor").GetInt64() > 0);
        Assert.False(feed.GetProperty("more").GetBoolean());
    }
}
