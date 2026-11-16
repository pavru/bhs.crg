using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Ports;
using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Work.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Журнал отчёта монтажника (change <c>work-14-report-journal</c>; ТЗ WORK-11, WORK-12, WORK-31, WORK-51, CORE-28):
/// что и кто изменил, правка после подачи, одна транзакция с правкой, запрет правки записи, дубль в журнал ядра и
/// резервная копия. Сотрудники — вымышленные.
/// </summary>
[Collection(AssignmentHost.Collection)]
public class ReportJournalTests(AssignmentHost host) : IAsyncLifetime
{
    private const string Url = ReportEndpoints.Path;
    private readonly ReportWorld world = new(host);

    public Task InitializeAsync() => world.SeedAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Правка объёма 120 → 150: запись называет строку, поле, оба значения, автора и оба времени.</summary>
    [Fact]
    public async Task Правка_объёма_даёт_запись_с_полем_и_значениями()
    {
        var (employee, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var version = await CreateAsync(client, world.Draft(id, world.SiteA, lineId: line, deviceTime: "2026-11-16T18:00:00+03:00"));

        var put = await ReportWorld.PutAsync(client, $"{Url}/{id}", version, world.Draft(id, world.SiteA, lineId: line,
            volume: 150m, changeId: Guid.NewGuid(), deviceTime: "2026-11-16T19:00:00+03:00"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var items = await JournalAsync(client, id);
        Assert.Equal(["created", "updated"], items.Select(i => i.GetProperty("kind").GetString()));
        var edit = items[1];
        var change = Assert.Single(edit.GetProperty("changes").EnumerateArray());
        Assert.Equal(line.ToString(), change.GetProperty("line").GetString());
        Assert.Equal("volume", change.GetProperty("field").GetString());
        Assert.Equal(120m, change.GetProperty("before").GetDecimal());
        Assert.Equal(150m, change.GetProperty("after").GetDecimal());
        Assert.Equal("Петров Пётр Петрович", edit.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal(employee, edit.GetProperty("executor").GetProperty("employeeId").GetGuid());
        Assert.False(edit.GetProperty("enteredFor").GetBoolean());
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 16, 0, 0, TimeSpan.Zero), edit.GetProperty("deviceTime").GetDateTimeOffset());
        Assert.NotEqual(default, edit.GetProperty("serverTime").GetDateTimeOffset());
        Assert.False(edit.GetProperty("afterSubmission").GetBoolean());
        Assert.Contains(items[0].GetProperty("changes").EnumerateArray(),
            c => c.GetProperty("field").GetString() == "volume" && c.GetProperty("before").ValueKind == JsonValueKind.Null);
    }

    /// <summary>Менеджер Иванов вводит отчёт Петрова: автор — Иванов, исполнитель — Петров, <c>enteredFor: true</c>.</summary>
    [Fact]
    public async Task Ввод_за_сотрудника_назван_в_журнале()
    {
        var (_, manager) = await world.ManagerAsync("Иванов Иван Иванович", world.SiteA);
        var petrov = await world.EmployeeAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var created = await manager.PostAsJsonAsync(EntryReportEndpoints.Path, world.Draft(id, world.SiteA, employee: petrov));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var entry = Assert.Single(await JournalAsync(manager, id));
        Assert.Equal("Иванов Иван Иванович", entry.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal(petrov, entry.GetProperty("executor").GetProperty("employeeId").GetGuid());
        Assert.Equal("Петров Пётр Петрович", entry.GetProperty("executor").GetProperty("name").GetString());
        Assert.True(entry.GetProperty("enteredFor").GetBoolean());
    }

    /// <summary>
    /// Момент подачи — 00:00 17.11 (МСК). Правка в 09:00 17.11 — правка после подачи и запись в журнале ядра; правка,
    /// сделанная в 23:30 16.11 и доставленная позже, — обычная доставка, без пометки.
    /// </summary>
    [Fact]
    public async Task Правка_после_подачи_по_времени_устройства()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        await CreateAsync(client, world.Draft(id, world.SiteA, lineId: line));
        await SubmitAsync(id, new DateTimeOffset(2026, 11, 16, 21, 0, 0, TimeSpan.Zero));
        // Момент подачи поставлен мимо адресов — версия строки сдвинулась: правка берёт её чтением.
        var version = (await ReportWorld.JsonAsync(await client.GetAsync($"{Url}/{id}"))).GetProperty("version").GetString();

        var late = await ReportWorld.JsonAsync(await ReportWorld.PutAsync(client, $"{Url}/{id}", version, world.Draft(id,
            world.SiteA, lineId: line, volume: 150m, changeId: Guid.NewGuid(), deviceTime: "2026-11-17T09:00:00+03:00")));
        await ReportWorld.PutAsync(client, $"{Url}/{id}", late.GetProperty("version").GetString(), world.Draft(id,
            world.SiteA, lineId: line, volume: 140m, changeId: Guid.NewGuid(), deviceTime: "2026-11-16T23:30:00+03:00"));

        var items = await JournalAsync(client, id);
        Assert.Equal([false, true, false], items.Select(i => i.GetProperty("afterSubmission").GetBoolean()));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 20, 30, 0, TimeSpan.Zero), items[2].GetProperty("deviceTime").GetDateTimeOffset());
        var core = await CoreRecordsAsync(id);
        var edited = Assert.Single(core);
        Assert.Equal(ReportActions.EditedAfterSubmission.Code, edited.Action);
        Assert.Contains("volume", edited.After);
    }

    /// <summary>
    /// Сбой сохранения после того, как запись журнала подготовлена: номер, который получит правка, заранее занят в
    /// протоколе, и второе сохранение (протокол и журнал) падает. Нет ни правки, ни записи журнала.
    /// </summary>
    [Fact]
    public async Task Сбой_сохранения_не_оставляет_ни_правки_ни_записи()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var version = await CreateAsync(client, world.Draft(id, world.SiteA, lineId: line));
        await SqlAsync(
            "INSERT INTO work.report_changes (seq, report_id, employee_id, construction_id, change_id, kind, server_time) " +
            "SELECT last_value + 1, gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'updated', now() " +
            "FROM work.report_seq");

        var put = await ReportWorld.PutAsync(client, $"{Url}/{id}", version, world.Draft(id, world.SiteA, lineId: line,
            volume: 150m, changeId: Guid.NewGuid()));

        Assert.NotEqual(HttpStatusCode.OK, put.StatusCode);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        Assert.Equal(120m, await db.Set<ReportLine>().Where(l => l.Id == line).Select(l => l.Volume).SingleAsync());
        Assert.Equal([ReportJournalKinds.Created],
            await db.Set<ReportJournalEntry>().Where(e => e.ReportId == id).Select(e => e.Kind).ToListAsync());
    }

    /// <summary>Код модуля меняет или удаляет загруженную запись журнала — сохранение отвергается, запись цела.</summary>
    [Fact]
    public async Task Запись_журнала_не_правится_и_не_удаляется()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        await CreateAsync(client, world.Draft(id, world.SiteA));

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
            var entry = await db.Set<ReportJournalEntry>().SingleAsync(e => e.ReportId == id);
            db.Entry(entry).Property(nameof(ReportJournalEntry.AuthorName)).CurrentValue = "Подмена";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            db.Remove(await db.Set<ReportJournalEntry>().SingleAsync(e => e.ReportId == id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        using var check = host.Services.CreateScope();
        var kept = await check.ServiceProvider.GetRequiredService<WorkDbContext>().Set<ReportJournalEntry>().AsNoTracking()
            .SingleAsync(e => e.ReportId == id);
        Assert.Equal("Петров Пётр Петрович", kept.AuthorName);
    }

    /// <summary>
    /// Удаление черновика — запись журнала отчёта с удалённым и запись журнала ядра «черновик отчёта удалён» с датой,
    /// сменой и стройкой.
    /// </summary>
    [Fact]
    public async Task Удаление_черновика_пишет_журнал_ядра()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var version = await CreateAsync(client, world.Draft(id, world.SiteA, shift: "night"));

        Assert.Equal(HttpStatusCode.NoContent, (await ReportWorld.DeleteAsync(client, $"{Url}/{id}", version)).StatusCode);

        var record = Assert.Single(await CoreRecordsAsync(id));
        Assert.Equal(ReportActions.Deleted.Code, record.Action);
        Assert.Equal("Петров Пётр Петрович — Стройка А, 16.11.2026, ночь", record.TargetLabel);
        var deleted = (await EntriesAsync(id)).Single(e => e.Kind == ReportJournalKinds.Deleted);
        using var changes = JsonDocument.Parse(deleted.Changes);
        Assert.Contains(changes.RootElement.EnumerateArray(),
            c => c.GetProperty("field").GetString() == "volume" && c.GetProperty("after").ValueKind == JsonValueKind.Null);
    }

    /// <summary>Сбой записи в журнал ядра не отменяет удаления: свидетельство уже в журнале отчёта.</summary>
    [Fact]
    public async Task Сбой_журнала_ядра_не_откатывает_удаление()
    {
        var (employee, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var id = Guid.NewGuid();
        var version = await CreateAsync(client, world.Draft(id, world.SiteA));

        using (var scope = host.Services.CreateScope())
        {
            var activity = ActivatorUtilities.CreateInstance<ReportActivity>(scope.ServiceProvider, new BrokenLog());
            var desk = ActivatorUtilities.CreateInstance<ReportDesk>(scope.ServiceProvider, activity);
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            http.Request.Headers.IfMatch = version;
            var result = await desk.RemoveDraftAsync(ReportViewer.Own(employee), id, null, null, http.Request, default);
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
            Assert.Equal(StatusCodes.Status204NoContent, ((IStatusCodeHttpResult)result).StatusCode);
        }

        Assert.Equal(0, await world.ReportCountAsync());
        Assert.Contains(await EntriesAsync(id), e => e.Kind == ReportJournalKinds.Deleted);
        Assert.Empty(await CoreRecordsAsync(id));
    }

    /// <summary>Журнал едет в копию схемы модуля и возвращается из неё запись в запись.</summary>
    [Fact]
    public async Task Копия_и_восстановление_сохраняют_журнал()
    {
        var (_, client) = await world.InstallerAsync("Петров Пётр Петрович");
        var (id, line) = (Guid.NewGuid(), Guid.NewGuid());
        var version = await CreateAsync(client, world.Draft(id, world.SiteA, lineId: line));
        await ReportWorld.PutAsync(client, $"{Url}/{id}", version, world.Draft(id, world.SiteA, lineId: line, volume: 150m,
            changeId: Guid.NewGuid(), deviceTime: "2026-11-16T19:00:00+03:00"));
        var before = await JournalRowsAsync();
        Assert.Equal(2, before.Count);

        BackupModuleSchema[] sections;
        using (var scope = host.Services.CreateScope())
        {
            await using var conn = new NpgsqlConnection(host.ConnectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            sections = await scope.ServiceProvider.GetRequiredService<IModuleSchemaBackup>().ReadAsync(tx, [], default);
            await tx.CommitAsync();
        }
        var work = sections.Single(s => s.Schema == WorkDbContext.SchemaName);
        Assert.Equal(2, work.Tables.Single(t => t.Table == "report_journal").Rows.Length);

        await SqlAsync("DELETE FROM work.report_journal");
        Assert.Empty(await JournalRowsAsync());

        using (var scope = host.Services.CreateScope())
        {
            await using var conn = new NpgsqlConnection(host.ConnectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            var warnings = new List<string>();
            await scope.ServiceProvider.GetRequiredService<IModuleSchemaBackup>().RestoreAsync(sections, tx, warnings, default);
            await tx.CommitAsync();
        }

        Assert.Equal(before, await JournalRowsAsync());
    }

    private async Task<string?> CreateAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync(Url, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReportWorld.JsonAsync(response)).GetProperty("version").GetString();
    }

    private static async Task<List<JsonElement>> JournalAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"{Url}/{id}/journal");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await ReportWorld.JsonAsync(response)).GetProperty("items").EnumerateArray()];
    }

    /// <summary>
    /// Поставить подачу прямо в базе — статус и момент одной записью, как это делает подача (<c>work-15</c>): журналу
    /// нужен точный момент, а адрес подачи ограничил бы его концом окна.
    /// </summary>
    internal static async Task SubmitAsync(AssignmentHost host, Guid id, DateTimeOffset at)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkDbContext>();
        Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE work.reports SET status = 'submitted', submitted_by = 'manual', submitted_at = {at.ToUniversalTime()} WHERE id = {id}"));
    }

    private Task SubmitAsync(Guid id, DateTimeOffset at) => SubmitAsync(host, id, at);

    private async Task SqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(host.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<string>> JournalRowsAsync()
    {
        await using var conn = new NpgsqlConnection(host.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT to_jsonb(j)::text FROM work.report_journal j ORDER BY seq", conn);
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows;
    }

    private async Task<List<ReportJournalEntry>> EntriesAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<WorkDbContext>().Set<ReportJournalEntry>().AsNoTracking()
            .Where(e => e.ReportId == id).OrderBy(e => e.Seq).ToListAsync();
    }

    private async Task<List<BHS.CRG.Domain.Activity.ActivityRecord>> CoreRecordsAsync(Guid id)
    {
        using var scope = host.Services.CreateScope();
        var target = id.ToString();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().ActivityRecords.AsNoTracking()
            .Where(r => r.TargetId == target && r.Action.StartsWith("work.report.")).ToListAsync();
    }

    private sealed class BrokenLog : IModuleActivityLog
    {
        public Task RecordAsync(ModuleActivityAction action, string? targetId = null, string? targetLabel = null,
            string? before = null, string? after = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("журнал действий недоступен");
    }
}
