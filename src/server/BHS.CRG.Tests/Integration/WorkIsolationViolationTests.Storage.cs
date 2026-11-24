using BHS.CRG.Domain.Activity;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

public sealed partial class WorkIsolationViolationTests
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

    [Fact]
    public async Task Содержимое_отчёта_остаётся_в_схеме_модуля()
    {
        var (a, b, manager, unassigned, executive) = await SeedAsync();
        using (a)
        using (b)
        using (manager)
        using (unassigned)
        using (executive)
        {
            await new ReviewWorld(testHost).TickAsync(ReviewWorld.Up19.ToUniversalTime());
            var notices = await ReviewWorld.BellAsync(manager);
            Assert.Contains(notices, notice => notice.Message.Contains("Стройка Б", StringComparison.Ordinal));
            Assert.DoesNotContain(notices, notice => notice.Message.Contains(marker, StringComparison.Ordinal));
            await ArchiveReportAsync(b, manager);
        }

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

        // Запись синтетического значения проверяет сам поиск по базе и выполняется последней.
        await using var transaction = await core.Database.BeginTransactionAsync();
        core.ActivityRecords.Add(ActivityRecord.Create("work25.test", null, "Сторож хранения", targetLabel: marker));
        await core.SaveChangesAsync();
        var marked = await MarkedColumnsAsync(db, marker);
        var failure = Assert.Throws<InvalidOperationException>(() => AssertNoCoreMarker(marked));
        Assert.Contains("public.activity_log.TargetLabel", failure.Message, StringComparison.Ordinal);
        await transaction.RollbackAsync();
    }

    private async Task ArchiveReportAsync(HttpClient owner, HttpClient manager)
    {
        var path = $"{ReportEndpoints.Path}/{reportB}";
        var report = await owner.GetFromJsonAsync<JsonElement>(path);
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
                reason = "Уточнены часы в синтетическом отчёте",
                lines = new[] { new { id = lineB, workTypeId = world.WorkType, unitId = world.Unit, volume = 100m,
                    location = marker, comment = marker, details = new { work25 = marker }, isCrew = true, crewRole = "lead",
                    materials = report.GetProperty("lines")[0].GetProperty("materials") } },
            };
            using var corrected = await manager.SendAsync(AssignmentWorld.Versioned(HttpMethod.Post, $"{path}/correction", version, changed));
            Assert.Equal(2, (await ExpectJsonAsync(corrected, $"{path}/correction")).GetProperty("currentVersion").GetInt32());
            await CheckResponseAsync(owner, $"{path}?view=at-closing", Ok, marker, canReadReport: true);
        }
        finally
        {
            testHost.ClosureTime = null;
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
