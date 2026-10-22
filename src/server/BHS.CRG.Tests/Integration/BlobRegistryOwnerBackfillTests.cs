using System.Text;
using BHS.CRG.Domain.Storage;
using BHS.CRG.Infrastructure.Maintenance;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Разовый сбор владельцев (ТЗ CORE-37.2): файлы, лежавшие до колонки владельца, раздаются по схеме
/// колонки, где найден путь, а образ — по владельцу оригинала.
///
/// <para>Схема модуля здесь своя, временная — <c>costs_ownertest</c>: имя схемы модуля начинается с
/// его кода и подчёркивания (<c>ModuleSchema.Validate</c>), и сбор обязан понимать обе формы. Таблица
/// несёт путь и текстом, и внутри JSONB — модуль хранит путь где хочет, и копия находит его так же.
/// Схемой модуля её делает таблица истории миграций; соседняя схема без неё — чужая (расширение,
/// оставленная руками), и путь вложения ядра в ней ничего не меняет.</para>
/// </summary>
[Collection("Integration")]
public class BlobRegistryOwnerBackfillTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    private const string Schema = "costs_ownertest";
    private const string Stray = "stray_ownertest";

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public async Task DisposeAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA IF EXISTS {Schema} CASCADE");
        await db.Database.ExecuteSqlRawAsync($"DROP SCHEMA IF EXISTS {Stray} CASCADE");
    }

    private static Stream Content(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Файлы_модуля_и_их_образы_получают_модуль_остальное_остаётся_ядру_повтор_ничего_не_меняет()
    {
        using var scope = fixture.Services.CreateScope();
        var blobs = scope.ServiceProvider.GetRequiredService<BHS.CRG.Application.Common.IBlobStorage>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Всё кладётся от имени ядра — так лежали файлы до колонки владельца.
        var scan = await blobs.UploadAsync("скан.pdf", Content("скан"), "application/pdf");
        var inJson = await blobs.UploadAsync("выгрузка.xml", Content("1С"), "application/xml");
        var image = await blobs.UploadAsync("rendition.pdf", Content("образ"), "application/pdf");
        var attachment = await blobs.UploadAsync("вложение.pdf", Content("ядро"), "application/pdf");

        await db.Database.ExecuteSqlRawAsync($"""
            CREATE SCHEMA {Schema};
            CREATE TABLE {Schema}."__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32));
            CREATE TABLE {Schema}.invoices ("Id" uuid PRIMARY KEY, "ScanPath" text, "Extra" jsonb);
            CREATE SCHEMA {Stray};
            CREATE TABLE {Stray}.notes ("Id" uuid PRIMARY KEY, "Path" text);
            """);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO costs_ownertest.invoices VALUES
                (gen_random_uuid(), {scan}, jsonb_build_object('files', jsonb_build_array({inJson})))
            """);
        await db.Database.ExecuteSqlAsync($"INSERT INTO stray_ownertest.notes VALUES (gen_random_uuid(), {attachment})");
        db.Renditions.Add(RenditionRecord.Built(scan, image, 1, [], "test"));
        await db.SaveChangesAsync();

        var backfill = scope.ServiceProvider.GetRequiredService<BlobRegistryOwnerBackfill>();
        Assert.Equal(3, await backfill.RunAsync());

        db.ChangeTracker.Clear();
        var owners = await db.BlobRegistry.AsNoTracking().ToDictionaryAsync(e => e.Path, e => e.Owner);
        Assert.Equal("costs", owners[scan]);
        Assert.Equal("costs", owners[inJson]);
        Assert.Equal("costs", owners[image]);
        Assert.Equal(BlobOwner.Core, owners[attachment]);

        // Повтор ничего не меняет: сбор отбирает только «ядро», а файлы модуля уже названы.
        Assert.Equal(0, await backfill.RunAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(owners, await db.BlobRegistry.AsNoTracking().ToDictionaryAsync(e => e.Path, e => e.Owner));
    }
}
