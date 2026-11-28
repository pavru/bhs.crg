using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Notifications;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WorkContentRollbackBarrier = BHS.CRG.Infrastructure.Migrations.WorkContentRollbackBarrier;

namespace BHS.CRG.Tests.Integration;

[Collection(AssignmentHost.Collection)]
public class WorkContentStorageTests(AssignmentHost host)
{
    [Fact]
    public async Task Обслуживание_сохраняет_историю_и_метаданные()
    {
        var world = new ReviewWorld(host);
        await world.SeedAsync();
        var (employee, installer) = await world.Reports.InstallerAsync("Монтажник");
        var (_, manager) = await world.Reports.ManagerAsync("Менеджер", world.SiteA);
        using var owner = installer;
        using var reviewer = manager;
        var reportId = Guid.NewGuid();
        var marker = $"Синтетический комментарий {Guid.NewGuid():N}";
        var submitted = await world.SubmittedAsync(owner, reportId, world.SiteA);
        using var result = await ReviewWorld.ReturnAsync(reviewer, reportId, ReviewWorld.Version(submitted), marker);
        result.EnsureSuccessStatusCode();

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var current = await db.Notifications.AsNoTracking().SingleAsync(n => n.LinkUrl == $"/my-reports/{reportId}");
        var workEntry = Assert.Single(await world.EntriesAsync(reportId), e => e.Kind == ReportJournalKinds.Returned);
        var audit = Assert.Single(await world.CoreRecordsAsync(ReportActions.Returned.Code, reportId));
        Assert.Equal(marker, workEntry.Comment);
        Assert.Equal(workEntry.AuthorName, audit.ActorName);
        Assert.DoesNotContain(marker, current.Message);

        var notice = Notification.Create(NotificationSeverity.Warning, current.Title,
            $"Стройка А, день. {workEntry.AuthorName}: {workEntry.Comment}", null, current.UserId);
        var state = NotificationUserState.Create(notice.Id, current.UserId!.Value, isRead: true);
        var unrelatedSource = Notification.Create(NotificationSeverity.Warning, notice.Title, marker,
            "Другой издатель", current.UserId);
        var unrelatedTitle = Notification.Create(NotificationSeverity.Warning, $"{notice.Title} (копия)", marker,
            null, current.UserId);
        var oldReturn = ActivityRecord.Create(audit.Action, audit.ActorId, audit.ActorName, audit.TargetId,
            audit.TargetLabel, audit.Before, marker, occurredAt: audit.OccurredAt,
            sessionId: Guid.NewGuid(), seriesHead: false, channel: ActivityChannel.FromStored(audit.Channel));
        var unrelatedAudit = ActivityRecord.Create(ReportActions.Accepted.Code, audit.ActorId,
            audit.ActorName, audit.TargetId, audit.TargetLabel, "подан", marker);
        var dataset = DataSetFile.CreateSystem("Старый набор", CatalogScope.System, null);
        var source = dataset.AddSource("Строки", "system:table:work.lines?columns=Место",
            JsonSerializer.Serialize(new[] { new { name = "Место", sampleValues = new[] { marker } } }), 1);
        var binding = DataSetBinding.For(employee, source.Id, null, """{"Участок":"Место"}""");
        db.AddRange(notice, state, unrelatedSource, unrelatedTitle, oldReturn, unrelatedAudit, dataset, binding);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var auditIds = new[] { oldReturn.Id, unrelatedAudit.Id, audit.Id };
        var noticeIds = new[] { notice.Id, unrelatedSource.Id, unrelatedTitle.Id, current.Id };
        var eventsBefore = await AuditAsync();
        var notificationsBefore = await NoticesAsync();
        var stateBefore = JsonSerializer.Serialize(await db.NotificationUserStates.AsNoTracking().SingleAsync(s => s.Id == state.Id));
        var original = JsonSerializer.Serialize(await world.EntriesAsync(reportId));

        using var restarted = host.WithWebHostBuilder(_ => { });
        _ = restarted.Services; // Запуск существующей базы через настоящий старт API.
        var eventsAfter = await AuditAsync();
        var notificationsAfter = await NoticesAsync();
        Assert.Equal(eventsBefore[unrelatedAudit.Id], eventsAfter[unrelatedAudit.Id]);
        Assert.Equal(eventsBefore[audit.Id], eventsAfter[audit.Id]);
        AssertPropertyChanged(eventsBefore[oldReturn.Id], eventsAfter[oldReturn.Id], "After", "возвращён");
        AssertPropertyChanged(notificationsBefore[notice.Id], notificationsAfter[notice.Id], "Message",
            "Комментарий к возврату доступен в журнале отчёта.");
        foreach (var id in new[] { unrelatedSource.Id, unrelatedTitle.Id, current.Id })
            Assert.Equal(notificationsBefore[id], notificationsAfter[id]);
        Assert.Equal(stateBefore, JsonSerializer.Serialize(await db.NotificationUserStates.AsNoTracking().SingleAsync(s => s.Id == state.Id)));
        Assert.Equal(original, JsonSerializer.Serialize(await world.EntriesAsync(reportId)));
        var cached = await db.DataSetSources.AsNoTracking().SingleAsync(s => s.Id == source.Id);
        Assert.DoesNotContain(marker, cached.CachedSchema);
        Assert.Equal(DataSetSource.UnstoredRowCount, cached.CachedRowCount);
        Assert.Equal(binding.SourceId, (await db.DataSetBindings.AsNoTracking().SingleAsync(b => b.Id == binding.Id)).SourceId);

        await WorkContentMaintenance.AtStartupAsync(db, NullLogger.Instance);
        Assert.Equal(eventsAfter.OrderBy(x => x.Key), (await AuditAsync()).OrderBy(x => x.Key));
        Assert.Equal(notificationsAfter.OrderBy(x => x.Key), (await NoticesAsync()).OrderBy(x => x.Key));

        var saved = await db.ActivityRecords.SingleAsync(a => a.Id == oldReturn.Id);
        db.Entry(saved).Property(e => e.After).CurrentValue = marker;
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.Remove(saved);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());

        async Task<Dictionary<Guid, string>> AuditAsync() =>
            (await db.ActivityRecords.AsNoTracking().Where(a => auditIds.Contains(a.Id)).ToListAsync())
            .ToDictionary(a => a.Id, a => JsonSerializer.Serialize(a));

        async Task<Dictionary<Guid, string>> NoticesAsync() =>
            (await db.Notifications.AsNoTracking().Where(n => noticeIds.Contains(n.Id)).ToListAsync())
            .ToDictionary(n => n.Id, n => JsonSerializer.Serialize(n));
    }

    private static void AssertPropertyChanged(string before, string after, string property, string value)
    {
        var fields = JsonNode.Parse(before)!.AsObject();
        fields[property] = value;
        Assert.True(JsonNode.DeepEquals(fields, JsonNode.Parse(after)));
    }
}

public sealed class WorkContentStorageCoreHost : ModuleOnlyHost
{
    protected override string ModuleCode => "id";
    protected override string DatabaseSuffix => "work30core";
}

public class WorkContentStorageRestoreTests(WorkContentStorageCoreHost host) : IClassFixture<WorkContentStorageCoreHost>
{
    [Fact]
    public async Task Рубеж_отката_записан_в_журнале_при_старте_без_Work()
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, id => id.EndsWith($"_{nameof(WorkContentRollbackBarrier)}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Старая_копия_очищается_при_выключенном_Work()
    {
        await host.ResetDatabaseAsync();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
        var now = DateTimeOffset.UtcNow;
        var file = new BackupDataSetFile(Guid.NewGuid(), "Системный набор", "System", "system", "System", null,
            null, null, null, null, now, now);
        var marker = $"Work-30-{Guid.NewGuid():N}";
        var schema = JsonSerializer.Serialize(new[] { new { name = "Колонка", sampleValues = new[] { marker } } });
        var reports = new BackupDataSetSource(Guid.NewGuid(), file.Id, "Отчёты", "System:Table:work.reports",
            """[{"name":"Колонка","expr":"."}]""", schema, 5, JsonSerializer.Serialize(new[] { marker }), null,
            """{"op":"and","children":[]}""", "[]", """[{"column":"Колонка","direction":"asc"}]""", null,
            null, null, null, null, now, now);
        var lines = reports with { Id = Guid.NewGuid(), Name = "Работы", SheetOrPath = "system:table:work.lines?columns=Колонка" };
        var other = reports with { Id = Guid.NewGuid(), Name = "Контроль", SheetOrPath = SystemDataSets.SetDocumentsMarker };
        var audit = new BackupActivityRecord(Guid.NewGuid(), now, ReportActions.Corrected.Code, Guid.NewGuid(),
            "Автор", Guid.NewGuid().ToString(), "Отчёт", null, marker, Guid.NewGuid(), false, "mcp");
        var foreign = audit with { Id = Guid.NewGuid(), Action = ReportActions.Accepted.Code };
        var manifest = new BackupManifest(BackupService.CurrentSchemaVersion, "2.140.1", now,
            [], [], [], [], DataSetFiles: [file], DataSetSources: [reports, lines, other], ActivityLog: [audit, foreign]);
        using var copy = new MemoryStream();
        using (var zip = new ZipArchive(copy, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var entry = zip.CreateEntry("manifest.json").Open();
            await JsonSerializer.SerializeAsync(entry, manifest);
        }

        for (var repeat = 0; repeat < 2; repeat++)
        {
            copy.Position = 0;
            var restore = await backup.ImportAsync(copy);
            Assert.True(restore.Success, string.Join(Environment.NewLine, restore.Warnings));
            foreach (var source in new[] { reports, lines, other })
            {
                var saved = await db.DataSetSources.AsNoTracking().SingleAsync(s => s.Id == source.Id);
                Assert.Equal(source.Name, saved.Name);
                Assert.Equal(source.FileId, saved.FileId);
                Assert.Equal(source.SheetOrPath, saved.SheetOrPath);
                Assert.True(JsonEquals(source.ColumnExpressions, saved.ColumnExpressions));
                Assert.True(JsonEquals(source.RowFilter, saved.RowFilter));
                Assert.True(JsonEquals(source.ComputedColumns, saved.ComputedColumns));
                Assert.True(JsonEquals(source.SortSpec, saved.SortSpec));
                if (source.Id == other.Id)
                {
                    Assert.Contains(marker, saved.CachedSchema);
                    Assert.Equal(5, saved.CachedRowCount);
                    continue;
                }
                using var parsed = JsonDocument.Parse(saved.CachedSchema);
                var column = Assert.Single(parsed.RootElement.EnumerateArray());
                Assert.Equal("Колонка", column.GetProperty("name").GetString());
                Assert.Empty(column.GetProperty("sampleValues").EnumerateArray());
                Assert.Equal(DataSetSource.UnstoredRowCount, saved.CachedRowCount);
                Assert.Null(saved.CachedData);
            }

            var entry = await db.ActivityRecords.AsNoTracking().SingleAsync(a => a.Id == audit.Id);
            Assert.Equal("исправлен", entry.After);
            Assert.Equal(audit.ActorId, entry.ActorId);
            Assert.Equal(audit.TargetId, entry.TargetId);
            Assert.Equal(audit.OccurredAt.ToUnixTimeMilliseconds(), entry.OccurredAt.ToUnixTimeMilliseconds());
            Assert.Equal(audit.SessionId, entry.SessionId);
            Assert.Equal(audit.SeriesHead, entry.SeriesHead);
            Assert.Equal(audit.Channel, entry.Channel);
            Assert.Equal(marker, (await db.ActivityRecords.AsNoTracking().SingleAsync(a => a.Id == foreign.Id)).After);
        }
    }

    private static bool JsonEquals(string? expected, string? actual) =>
        JsonNode.DeepEquals(JsonNode.Parse(expected!), JsonNode.Parse(actual!));
}
