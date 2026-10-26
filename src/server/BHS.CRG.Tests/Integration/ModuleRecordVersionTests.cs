using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Базовый тип версии записи живьём (ТЗ CORE-36, change <c>plan-12-record-versions</c>): правку прежней
/// версии отвергает контекст модуля, второй номер в записи — единственный индекс из
/// <see cref="ModuleRecordVersion.Map{TVersion}" />.
///
/// <para>На ПОДДЕЛЬНОМ контексте модуля, как <see cref="ModuleSchemaTests" />: настоящих наследников в
/// этом change нет — первый приходит со своим модулем. Механизм настоящий, поддельна только таблица.</para>
/// </summary>
[Collection("Integration")]
public class ModuleRecordVersionTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string ProbeSchema = "record_version_probe";

    public async Task InitializeAsync()
    {
        _ = fixture.CreateClient();
        await DropProbeSchemaAsync();

        await using var db = ProbeContext();
        await using var conn = new NpgsqlConnection(IntegrationTestFixture.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(db.Database.GenerateCreateScript(), conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await DropProbeSchemaAsync();

    [Fact]
    public async Task Правка_и_удаление_версии_отвергаются()
    {
        var record = Guid.NewGuid();
        await using (var db = ProbeContext())
        {
            db.Versions.Add(new ProbeVersion(record, 1, """{"a":1}""", "причина"));
            await db.SaveChangesAsync();
        }

        await using (var db = ProbeContext())
        {
            var version = await db.Versions.SingleAsync(v => v.RecordId == record);
            db.Entry(version).Property(v => v.Reason).CurrentValue = "переписано";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var db = ProbeContext())
        {
            db.Versions.Remove(await db.Versions.SingleAsync(v => v.RecordId == record));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var db = ProbeContext())
            Assert.Equal("причина", (await db.Versions.SingleAsync(v => v.RecordId == record)).Reason);
    }

    [Fact]
    public async Task Второй_номер_в_записи_отвергается_индексом()
    {
        var record = Guid.NewGuid();
        await using (var db = ProbeContext())
        {
            db.Versions.Add(new ProbeVersion(record, 1, "{}", ""));
            db.Versions.Add(new ProbeVersion(Guid.NewGuid(), 1, "{}", ""));
            await db.SaveChangesAsync();
        }

        await using (var db = ProbeContext())
        {
            db.Versions.Add(new ProbeVersion(record, 1, "{}", "гонка"));
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, (refused.InnerException as PostgresException)?.SqlState);
        }

        await using (var db = ProbeContext())
            Assert.Equal(1, await db.Versions.CountAsync(v => v.RecordId == record));
    }

    private static ProbeModuleContext ProbeContext()
    {
        var options = new DbContextOptionsBuilder<ProbeModuleContext>();
        options.UseNpgsql(IntegrationTestFixture.TestConnectionString);
        return new ProbeModuleContext(options.Options);
    }

    private static async Task DropProbeSchemaAsync()
    {
        await using var conn = new NpgsqlConnection(IntegrationTestFixture.TestConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {ProbeSchema} CASCADE", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class ProbeModuleContext(DbContextOptions<ProbeModuleContext> options) : ModuleDbContext(options)
    {
        protected override string Schema => ProbeSchema;

        public DbSet<ProbeVersion> Versions => Set<ProbeVersion>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            ModuleRecordVersion.Map<ProbeVersion>(builder, "probe_versions");
        }
    }

    private sealed class ProbeVersion : ModuleRecordVersion
    {
        private ProbeVersion() { }

        public ProbeVersion(Guid record, int number, string content, string reason)
            : base(record, number, content, reason, null) { }
    }
}
