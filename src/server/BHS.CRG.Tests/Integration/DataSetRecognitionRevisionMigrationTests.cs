using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace BHS.CRG.Tests.Integration;

public class DataSetRecognitionRevisionMigrationTests : IAsyncLifetime
{
    private const string Suffix = "recognition_revision";
    private static readonly string Name = TestDatabases.Name(Suffix);

    public async Task InitializeAsync()
    {
        await DropAsync();
        await AdminAsync($"CREATE DATABASE {TestDatabases.Quote(Name)}");
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("BHS_TEST_KEEP_DB") is not { Length: > 0 }) await DropAsync();
    }

    [Fact]
    public async Task ExistingDataSetsGetDistinctRevisionsAndKeepTheirInputs()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(TestDatabases.ConnectionString(Suffix)).Options);
        var migrations = db.Database.GetMigrations().ToList();
        var index = migrations.FindIndex(m => m.EndsWith("_DataSetRecognitionRevision", StringComparison.Ordinal));
        Assert.True(index > 0);
        await db.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);

        const string grouping = """{"Groups":[],"ManuallyEdited":true}""";
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO dataset_files ("Id", "CreatedAt", "UpdatedAt", "Name", "Format", "BlobPath", "Scope", "Grouping")
            VALUES ({first}, now(), now(), 'Альбом', 'Pdf', 'album.pdf', 'System', CAST({grouping} AS jsonb)),
                   ({second}, now(), now(), 'Список', 'Csv', 'list.csv', 'System', NULL)
            """);

        await db.Database.MigrateAsync();

        var album = await db.DataSetFiles.AsNoTracking().SingleAsync(f => f.Id == first);
        var list = await db.DataSetFiles.AsNoTracking().SingleAsync(f => f.Id == second);
        Assert.NotEqual(Guid.Empty, album.RecognitionRevision);
        Assert.NotEqual(Guid.Empty, list.RecognitionRevision);
        Assert.NotEqual(album.RecognitionRevision, list.RecognitionRevision);
        Assert.Equal("Альбом", album.Name);
        Assert.Equal("album.pdf", album.BlobPath);
        Assert.True(BHS.CRG.Infrastructure.DataSets.GostGroupingSerialization.Parse(album.Grouping)!.ManuallyEdited);
        Assert.Equal("list.csv", list.BlobPath);
        Assert.Null(list.Grouping);
    }

    private static Task DropAsync() => AdminAsync($"DROP DATABASE IF EXISTS {TestDatabases.Quote(Name)} WITH (FORCE)");

    private static async Task AdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(IntegrationTestFixture.TestConnectionString)
        {
            Database = "postgres",
        }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
