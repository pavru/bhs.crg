using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Infrastructure.Recognition;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Полнота бэкапа (issue #403): конфигурация, от которой зависит генерация — переиспользуемые
/// перечисления (EnumType), ассеты шаблонов (TemplateAsset + их блобы) и общая Typst-библиотека
/// (TypstUserLib, синглтон) — должна пережить export→wipe→import. До #403 эти три сущности в
/// бэкап не попадали вовсе.
/// </summary>
[Collection("Integration")]
public class BackupServiceTests(IntegrationTestFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string AssetBlobPath = "assets/logo.png";
    private static readonly byte[] AssetBytes = [1, 2, 3, 4, 5];

    private BackupService Backup(IServiceScope scope) => new(
        scope.ServiceProvider.GetRequiredService<AppDbContext>(),
        scope.ServiceProvider.GetRequiredService<IBlobStorage>(),
        NullLogger<BackupService>.Instance);

    [Fact]
    public async Task Export_Import_RoundTrips_EnumTypes_TemplateAssets_And_TypstUserLib()
    {
        // ── Seed ──────────────────────────────────────────────────────────────
        var enumId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            var en = EnumType.Restore(enumId, "Статус", "status", "описание",
                JsonDocument.Parse("""[{"code":"a","label":"Активен"}]"""),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, group: "Группа");
            db.EnumTypes.Add(en);

            await blob.PutAsync(AssetBlobPath, new MemoryStream(AssetBytes), "image/png", default);
            var asset = TemplateAsset.Restore(assetId, TemplateAssetScope.System, null, TemplateAssetKind.Image,
                "logo", "logo.png", "image/png", AssetBlobPath, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            db.TemplateAssets.Add(asset);

            db.TypstUserLibs.Add(TypstUserLib.Create("#let hello() = [привет]"));
            await db.SaveChangesAsync();
        }

        // ── Export ────────────────────────────────────────────────────────────
        byte[] zipBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zip, _) = await Backup(scope).ExportAsync();
            using var ms = new MemoryStream();
            await zip.CopyToAsync(ms);
            zipBytes = ms.ToArray();
        }

        // Архив должен содержать файл ассета шаблона (раньше блоб терялся).
        using (var check = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            Assert.Contains(check.Entries, e => e.FullName == $"blobs/{AssetBlobPath}");

        // ── Симулируем чистое окружение: стираем БД и блоб ──────────────────────
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IBlobStorage>().DeleteAsync(AssetBlobPath);
        await fixture.ResetDatabaseAsync();

        // ── Import ────────────────────────────────────────────────────────────
        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await Backup(scope).ImportAsync(new MemoryStream(zipBytes));

        Assert.True(report.Success);
        Assert.Equal(1, report.EnumTypesCreated);
        Assert.Equal(1, report.TemplateAssetsCreated);
        Assert.True(report.TypstUserLibRestored);

        // ── Проверяем восстановленное состояние ─────────────────────────────────
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            var en = await db.EnumTypes.FirstOrDefaultAsync(e => e.Id == enumId);
            Assert.NotNull(en);
            Assert.Equal("status", en!.Code);
            Assert.Equal("Группа", en.Group);

            var asset = await db.TemplateAssets.FirstOrDefaultAsync(a => a.Id == assetId);
            Assert.NotNull(asset);
            Assert.Equal(AssetBlobPath, asset!.BlobPath);

            var lib = await db.TypstUserLibs.FirstOrDefaultAsync();
            Assert.NotNull(lib);
            Assert.Equal("#let hello() = [привет]", lib!.Content);

            // Файл ассета восстановлен в хранилище.
            await using var restored = await blob.DownloadAsync(AssetBlobPath);
            using var rms = new MemoryStream();
            await restored.CopyToAsync(rms);
            Assert.Equal(AssetBytes, rms.ToArray());
        }
    }

    [Fact]
    public async Task Export_Import_RoundTrips_RecognitionProfiles()
    {
        // Профили — конфигурация, влияющая на извлекаемые данные (issue #406), поэтому обязаны быть
        // в бэкапе. Важен и флаг IsModified: восстановленный правленый профиль не должен быть затёрт
        // сидингом на целевой системе.
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // ResetDatabaseAsync профили не чистит (это конфигурация) — снимаем пользовательские,
            // оставшиеся от прошлых прогонов, иначе счётчик восстановленных накапливается.
            db.RecognitionProfiles.RemoveRange(db.RecognitionProfiles.Where(p => p.Code == null));
            await db.SaveChangesAsync();
            await RecognitionProfileSeeder.SeedAsync(db);
            db.ChangeTracker.Clear();
            var custom = RecognitionProfile.Create(
                "Список деталей шкафа", RecognitionProfileKind.Table,
                fields: RecognitionProfileJson.WriteFields([]),
                rowColumns: RecognitionProfileJson.WriteFields([new RecognitionProfileField("Поз", "Позиция", "string")]),
                shape: RecognitionProfileJson.WriteShape(new RecognitionTableShape(TwoTierHeader: true)));
            db.RecognitionProfiles.Add(custom);
            await db.SaveChangesAsync();
        }

        byte[] zipBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zip, _) = await Backup(scope).ExportAsync();
            using var ms = new MemoryStream();
            await zip.CopyToAsync(ms);
            zipBytes = ms.ToArray();
        }

        // Чистое окружение: сносим профили (ResetDatabaseAsync их не трогает — это конфигурация).
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.RecognitionProfiles.RemoveRange(db.RecognitionProfiles);
            await db.SaveChangesAsync();
        }

        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await Backup(scope).ImportAsync(new MemoryStream(zipBytes));

        Assert.True(report.Success);
        Assert.Equal(1, report.RecognitionProfilesCreated);   // только пользовательский

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var restored = await db.RecognitionProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Name == "Список деталей шкафа");
            Assert.NotNull(restored);
            Assert.Equal(RecognitionProfileKind.Table, restored!.Kind);
            Assert.Null(restored.Code);          // пользовательский — кода нет
            Assert.False(restored.IsBuiltIn);
            Assert.True(RecognitionProfileJson.ReadShape(restored.Shape)!.TwoTierHeader);
            Assert.Contains(RecognitionProfileJson.ReadFields(restored.RowColumns), f => f.Name == "Поз");

            // Ловушка машины времени: НЕТРОНУТЫЕ встроенные профили копия НЕ восстанавливает — иначе
            // старая копия откатила бы улучшенный дефолт. Их переутверждает сидер при старте.
            Assert.Empty(await db.RecognitionProfiles.AsNoTracking()
                .Where(p => p.Code != null).ToListAsync());

            await RecognitionProfileSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
            Assert.NotNull(await db.RecognitionProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Code == BuiltInProfileCodes.CableJournal));
        }
    }

    [Fact]
    public async Task Import_OldBackupWithoutNewSections_DoesNotFail()
    {
        // Прежний v2-бэкап без новых секций (EnumTypes/TemplateAssets/TypstUserLib == null) — восстановим
        // без ошибок и без bump схемы (аддитивные nullable-поля).
        var manifest = new BackupManifest(
            SchemaVersion: BackupService.CurrentSchemaVersion,
            AppVersion: BackupService.CurrentAppVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            DocumentTypes: [], Templates: [], CatalogEntities: [], CommonDataEntries: []);

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("manifest.json");
            await using var w = entry.Open();
            await JsonSerializer.SerializeAsync(w, manifest, new JsonSerializerOptions { WriteIndented = true });
        }
        ms.Position = 0;

        using var scope = fixture.Services.CreateScope();
        var report = await Backup(scope).ImportAsync(ms);

        Assert.True(report.Success);
        Assert.Equal(0, report.EnumTypesCreated);
        Assert.Equal(0, report.TemplateAssetsCreated);
        Assert.False(report.TypstUserLibRestored);
    }
}
