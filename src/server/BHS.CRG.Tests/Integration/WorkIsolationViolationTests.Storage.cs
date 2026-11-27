using BHS.CRG.Api.Auth;
using BHS.CRG.Domain.Activity;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Work;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using BHS.CRG.Tests.Work;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

internal sealed partial class WorkIsolationScenario
{
    private static readonly Dictionary<string, string> NonPayload = new(StringComparer.Ordinal)
    {
        ["Assignment"] = "Назначения сотрудников на стройки, общие для смен.",
        ["CrewGroupRecord"] = "Ключ группы и её утверждённый итог. Содержимое строк читается из отчётов.",
        ["LinePhotoRemoval"] = "Идентификаторы снятого фото и строки, отметка времени.",
        ["ReportChange"] = "Курсор синхронизации, идентификаторы и версии изменений.",
        ["ReportLineParticipant"] = "Связь строки с идентификатором сотрудника, без его реквизитов.",
        ["ReviewDigestMark"] = "Отметка отправки сводки по идентификатору отчёта и времени подачи.",
        ["ReviewDigestRetry"] = "Очередь повторов по идентификаторам отчёта и адресата.",
    };

    private static readonly IReadOnlyDictionary<string, string[]> PayloadColumns = new Dictionary<string, string[]>
    {
        ["Report"] = ["note"],
        ["ReportLine"] = ["location", "comment", "details"],
        ["ReportLineMaterial"] = ["note"],
        ["LinePhoto"] = ["original_name"],
        ["ReportJournalEntry"] = ["changes"],
        ["ReportVersion"] = ["content"],
    };

    internal async Task CheckStorageAsync()
    {
        var returnComment = $"Work25-return-{reportB:N}";
        var correctionReason = $"Work25-correction-{reportB:N}";
        var review = new ReviewWorld(testHost);
        var (a, b, manager, unassigned, executive) = await SeedAsync();
        Guid[] publishedSources;
        using (a)
        using (b)
        using (manager)
        using (unassigned)
        using (executive)
        {
            publishedSources = await PublishSourcesForStorageCheckAsync();
            await ReturnAndResubmitAsync(b, manager, returnComment);
            await review.TickAsync(ReviewWorld.Up19.ToUniversalTime());
            var notices = await ReviewWorld.BellAsync(manager);
            Assert.Contains(notices, notice => notice.Message.Contains("Стройка Б", StringComparison.Ordinal));
            Assert.DoesNotContain(notices, notice => notice.Message.Contains(marker, StringComparison.Ordinal));
            await ArchiveReportAsync(b, manager, a, unassigned, executive, correctionReason);
        }
        var journal = await review.EntriesAsync(reportB);
        Assert.Equal(returnComment, Assert.Single(journal, entry => entry.Kind == ReportJournalKinds.Returned).Comment);
        Assert.Equal(correctionReason, Assert.Single(journal, entry => entry.Kind == ReportJournalKinds.Corrected).Comment);

        using var scope = testHost.Services.CreateScope();
        var work = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        var core = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entities = work.Model.GetEntityTypes().ToDictionary(type => type.ClrType.Name);
        AssertInventory(entities.Keys);
        var unseen = Assert.Throws<InvalidOperationException>(() => AssertInventory([.. entities.Keys, "NewReportPayload"]));
        Assert.Contains("NewReportPayload", unseen.Message, StringComparison.Ordinal);

        await core.Database.OpenConnectionAsync();
        var db = (NpgsqlConnection)core.Database.GetDbConnection();
        var columns = await MarkedColumnsAsync(db, marker);
        foreach (var (name, expected) in PayloadColumns)
        {
            var table = entities[name].GetTableName() ?? throw new InvalidOperationException($"У {name} нет таблицы.");
            foreach (var column in expected)
                Assert.Contains((WorkDbContext.SchemaName, table, column), columns);
        }
        AssertNoCoreMarker(columns);
        foreach (var text in new[] { returnComment, correctionReason })
            AssertNoCoreMarker(await MarkedColumnsAsync(db, text));

        var sources = await core.DataSetSources.AsNoTracking().Where(source => publishedSources.Contains(source.Id)).ToListAsync();
        Assert.Equal(publishedSources.Length, sources.Count);
        Assert.All(sources, source =>
        {
            Assert.Equal(DataSetSource.UnstoredRowCount, source.CachedRowCount);
            Assert.Null(source.CachedData);
            using var schema = JsonDocument.Parse(source.CachedSchema);
            var storedColumns = schema.RootElement.EnumerateArray();
            Assert.NotEmpty(storedColumns);
            Assert.All(storedColumns, column => Assert.Empty(column.GetProperty("sampleValues").EnumerateArray()));
        });

        // Запись синтетического значения проверяет сам поиск по базе и выполняется последней.
        await using var transaction = await core.Database.BeginTransactionAsync();
        core.ActivityRecords.Add(ActivityRecord.Create("work25.test", null, "Сторож хранения", targetLabel: marker));
        await core.SaveChangesAsync();
        var marked = await MarkedColumnsAsync(db, marker);
        var failure = Assert.Throws<InvalidOperationException>(() => AssertNoCoreMarker(marked));
        Assert.Contains("public.activity_log.TargetLabel", failure.Message, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }

    private async Task<Guid[]> PublishSourcesForStorageCheckAsync()
    {
        var (_, publisher) = await world.Assignments.UserAsync("Публикатор", WorkModule.ReportRead, CorePermissions.DataSetsEdit);
        using var publish = publisher;
        var reports = await PublishDataSetAsync(publish, WorkTableWorld.Reports, WorkTableWorld.ReportsMarker);
        var lines = await PublishDataSetAsync(publish, WorkTableWorld.Lines, WorkTableWorld.LinesMarker);
        return [reports.SourceId, lines.SourceId];
    }

    private async Task ReturnAndResubmitAsync(HttpClient owner, HttpClient manager, string comment)
    {
        var path = $"{ReportEndpoints.Path}/{reportB}";
        var submitted = await owner.GetFromJsonAsync<JsonElement>(path);
        using var response = await ReviewWorld.ReturnAsync(manager, reportB, ReviewWorld.Version(submitted), comment);
        var returned = await ExpectJsonAsync(response, $"{path}/return");
        Assert.Equal("returned", returned.GetProperty("status").GetString());

        using var notices = await owner.GetAsync("/api/notifications");
        var items = (await ExpectJsonAsync(notices, "/api/notifications")).GetProperty("items");
        var link = $"/my-reports/{reportB}";
        var notice = Assert.Single(items.EnumerateArray(), item => item.GetProperty("linkUrl").GetString() == link);
        Assert.DoesNotContain(comment, notice.GetProperty("message").GetString()!, StringComparison.Ordinal);
        await CrewWorld.SubmitAsync(owner, reportB, returned);
    }

    private async Task<(byte[] Original, byte[] Preview)> ReadStoredPhotoAsync()
    {
        var photo = Assert.Single(await new PhotoWorld(testHost).PhotosAsync(), photo => photo.Id == photoB);
        Assert.Equal(lineB, photo.LineId);
        Assert.Equal(LinePhotoState.Stored, photo.State);
        var blobs = testHost.Services.GetRequiredService<FakeBlobStorage>();
        var original = await ReadBlobAsync(Assert.IsType<string>(photo.BlobPath));
        var preview = await ReadBlobAsync(Assert.IsType<string>(photo.PreviewBlobPath));
        Assert.False(original.AsSpan().SequenceEqual(preview));
        return (original, preview);

        async Task<byte[]> ReadBlobAsync(string path)
        {
            await using var blob = await blobs.DownloadAsync(path);
            using var buffer = new MemoryStream();
            await blob.CopyToAsync(buffer);
            Assert.True(buffer.Length > 0, $"Пустой файл фото: {path}");
            return buffer.ToArray();
        }
    }

    private async Task ArchiveReportAsync(HttpClient owner, HttpClient manager, HttpClient installer,
        HttpClient unassigned, HttpClient executive, string correctionReason)
    {
        var path = $"{ReportEndpoints.Path}/{reportB}";
        var report = await owner.GetFromJsonAsync<JsonElement>(path);
        var storedPhoto = await ReadStoredPhotoAsync();
        using var accepted = await ReviewWorld.AcceptAsync(manager, reportB, ReviewWorld.Version(report));
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var version = ReviewWorld.Version(await ReportWorld.JsonAsync(accepted));
        using var closer = await new WorkPeriodWorld(testHost).CloserAsync();
        testHost.ClosureTime = ReviewWorld.Up19.ToUniversalTime();
        try
        {
            await WorkPeriodWorld.CloseAsync(closer, through: Day, site: world.SiteB, from: Day);
            testHost.ReportTime = testHost.ClosureTime;
            var changed = new
            {
                constructionId = world.SiteB, date = Day, shift = "day", hours = 9m, note = marker,
                reason = correctionReason,
                lines = new[] { new { id = lineB, workTypeId = world.WorkType, unitId = world.Unit, volume = VolumeB,
                    location = marker, comment = marker, details = new { work25 = marker }, isCrew = true, crewRole = "lead",
                    materials = report.GetProperty("lines")[0].GetProperty("materials"), photos = new[] { new { id = retainedPhotoB } } } },
            };
            using var corrected = await manager.SendAsync(AssignmentWorld.Versioned(HttpMethod.Post, $"{path}/correction", version, changed));
            var current = await ExpectJsonAsync(corrected, $"{path}/correction");
            Assert.Equal(2, current.GetProperty("currentVersion").GetInt32());
            Assert.Equal(9m, current.GetProperty("hours").GetDecimal());
            var currentPhoto = Assert.Single(current.GetProperty("lines")[0].GetProperty("photos").EnumerateArray());
            Assert.Equal(retainedPhotoB, currentPhoto.GetProperty("id").GetGuid());

            await CheckArchivedReadAsync(owner, installer, unassigned, executive, storedPhoto);
        }
        finally
        {
            testHost.ClosureTime = null;
        }
    }

    private async Task CheckArchivedReadAsync(HttpClient owner, HttpClient installer, HttpClient unassigned,
        HttpClient executive, (byte[] Original, byte[] Preview) storedPhoto)
    {
        var ownHistory = $"{ReportEndpoints.Path}/{reportB}?view=at-closing";
        var reviewHistory = $"/api/work/review/reports/{reportB}?number=1";
        await CheckResponseAsync(installer, ownHistory, Missing);
        await CheckResponseAsync(installer, reviewHistory, Closed);
        await CheckResponseAsync(unassigned, reviewHistory, Missing);

        var snapshot = await owner.GetFromJsonAsync<JsonElement>(ownHistory);
        Assert.Equal("at-closing", snapshot.GetProperty("view").GetString());
        Assert.Equal(HoursB, snapshot.GetProperty("hours").GetDecimal());
        var card = await executive.GetFromJsonAsync<JsonElement>(reviewHistory);
        Assert.Equal(1, card.GetProperty("viewedVersion").GetInt32());
        var archived = card.GetProperty("report");
        Assert.Equal(HoursB, archived.GetProperty("hours").GetDecimal());
        var photo = Assert.Single(archived.GetProperty("lines")[0].GetProperty("photos").EnumerateArray(),
            entry => entry.GetProperty("id").GetGuid() == photoB);
        var url = Assert.IsType<string>(photo.GetProperty("versionUrl").GetString());
        await CheckResponseAsync(executive, $"/api/work/photos/{photoB}", Missing);
        var preview = $"/api/work/photos/{photoB}/preview?reportId={reportB}&number=1";
        foreach (var reader in new[] { owner, executive })
        {
            await CheckResponseAsync(reader, url, Ok, expectedPhoto: storedPhoto.Original);
            await CheckResponseAsync(reader, preview, Ok, expectedPhoto: storedPhoto.Preview);
        }
        foreach (var archiveUrl in new[] { url, preview })
        {
            await CheckResponseAsync(installer, archiveUrl, Missing);
            await CheckResponseAsync(unassigned, archiveUrl, Missing);
        }
    }

    private static void AssertInventory(IEnumerable<string> entities)
    {
        var declared = PayloadColumns.Keys.Concat(NonPayload.Keys).ToHashSet(StringComparer.Ordinal);
        var live = entities.ToHashSet(StringComparer.Ordinal);
        if (!declared.SetEquals(live)) throw new InvalidOperationException(
            $"Нет в переписи: {string.Join(", ", live.Except(declared))}. Удалённые: {string.Join(", ", declared.Except(live))}.");
    }

    private static void AssertNoCoreMarker(IEnumerable<(string Schema, string Table, string Column)> columns)
    {
        var outside = columns.Where(found => found.Schema != WorkDbContext.SchemaName).ToArray();
        if (outside.Length != 0) throw new InvalidOperationException("Метка вне схемы work: " +
            string.Join(", ", outside.Select(found => $"{found.Schema}.{found.Table}.{found.Column}")));
    }

    private static async Task<List<(string Schema, string Table, string Column)>> MarkedColumnsAsync(NpgsqlConnection db, string marker)
    {
        await using var catalog = new NpgsqlCommand("""
            SELECT c.table_schema, c.table_name, c.column_name
            FROM information_schema.columns c
            JOIN information_schema.tables t USING (table_catalog, table_schema, table_name)
            WHERE t.table_type = 'BASE TABLE'
              AND c.table_schema NOT IN ('pg_catalog', 'information_schema')
              AND c.data_type IN ('text', 'character varying', 'character', 'json', 'jsonb')
            ORDER BY c.table_schema, c.table_name, c.ordinal_position
            """, db);
        var columns = new List<(string Schema, string Table, string Column)>();
        await using (var rows = await catalog.ExecuteReaderAsync())
            while (await rows.ReadAsync()) columns.Add((rows.GetString(0), rows.GetString(1), rows.GetString(2)));

        using var identifiers = new NpgsqlCommandBuilder();
        var found = new List<(string Schema, string Table, string Column)>();
        foreach (var table in columns.GroupBy(c => (c.Schema, c.Table)))
        {
            var names = table.Select(c => c.Column).ToArray();
            var checks = names.Select(name => $"coalesce(bool_or(strpos({identifiers.QuoteIdentifier(name)}::text, @marker) > 0), false)");
            var qualified = $"{identifiers.QuoteIdentifier(table.Key.Schema)}.{identifiers.QuoteIdentifier(table.Key.Table)}";
            await using var query = new NpgsqlCommand($"SELECT {string.Join(", ", checks)} FROM {qualified}", db);
            query.Parameters.AddWithValue("marker", marker);
            await using var hits = await query.ExecuteReaderAsync();
            Assert.True(await hits.ReadAsync(), $"Поиск не вернул результат для {qualified}.");
            for (var i = 0; i < names.Length; i++)
                if (hits.GetBoolean(i)) found.Add((table.Key.Schema, table.Key.Table, names[i]));
        }
        return found;
    }
}
