using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Признак архива записи справочника (issue #1185): кто его пишет, кто не пишет и как он переживает
/// резервную копию.
///
/// <para>Три способа потерять архив молча, и на каждый здесь тест: правка записи, прочитанной до
/// архива; восстановление копии, снятой версией без признака; восстановление поверх существующей
/// записи, где сущность пишется целиком.</para>
/// </summary>
public partial class BackupServiceTests
{
    private static readonly DateTimeOffset ArchivedOn = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private async Task<Guid> SeedCompositeTypeAsync()
    {
        var typeId = Guid.NewGuid();
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.DocumentTypes.Add(DocumentType.Restore(
            typeId, "Организация", $"org-{Guid.NewGuid():N}", DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
            false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
        await db.SaveChangesAsync();
        return typeId;
    }

    private async Task<Guid> SeedRecordAsync(Guid typeId, DateTimeOffset? archivedAt = null)
    {
        var id = Guid.NewGuid();
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.DomainObjects.Add(DomainObject.Restore(
            id, typeId, "ООО Ромашка", JsonDocument.Parse("{}"),
            CatalogScope.System, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, archivedAt));
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<DateTimeOffset?> ArchivedAtAsync(Guid id)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.DomainObjects.AsNoTracking().Where(o => o.Id == id).Select(o => o.ArchivedAt).SingleAsync();
    }

    private async Task<ArchiveOutcome> SetArchivedAsync(Guid id, bool archived)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRecordArchive>().SetAsync(id, archived);
    }

    private static BackupManifest ArchiveManifest(bool? knows, params BackupCommonDataEntry[] entries) =>
        new(SchemaVersion: Infrastructure.Backup.BackupService.CurrentSchemaVersion,
            AppVersion: Infrastructure.Backup.BackupService.CurrentAppVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            DocumentTypes: [], Templates: [], CatalogEntities: [],
            CommonDataEntries: entries,
            KnowsRecordArchive: knows);

    private static BackupCommonDataEntry Entry(Guid id, Guid typeId, DateTimeOffset? archivedAt) =>
        new(id, "ООО Ромашка", typeId, JsonDocument.Parse("{}").RootElement.Clone(),
            "System", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], archivedAt);

    [Fact]
    public async Task Archive_SetAndReturn_RepeatChangesNothing()
    {
        var id = await SeedRecordAsync(await SeedCompositeTypeAsync());

        Assert.Equal(ArchiveOutcome.Changed, await SetArchivedAsync(id, true));
        var first = await ArchivedAtAsync(id);
        Assert.NotNull(first);

        // Повтор — не новое архивирование: дата первого остаётся, служба отвечает «менять нечего».
        Assert.Equal(ArchiveOutcome.Unchanged, await SetArchivedAsync(id, true));
        Assert.Equal(first, await ArchivedAtAsync(id));

        Assert.Equal(ArchiveOutcome.Changed, await SetArchivedAsync(id, false));
        Assert.Null(await ArchivedAtAsync(id));
        Assert.Equal(ArchiveOutcome.Unchanged, await SetArchivedAsync(id, false));
    }

    /// <summary>Исход назван: адресу действия нельзя путать «нет записи» с безобидным повтором.</summary>
    [Fact]
    public async Task Archive_UnknownRecord_IsNotFound() =>
        Assert.Equal(ArchiveOutcome.NotFound, await SetArchivedAsync(Guid.NewGuid(), true));

    private async Task<Guid> SeedProfileAsync(Guid typeId, DateTimeOffset? archivedAt = null)
    {
        var id = await SeedRecordAsync(typeId, archivedAt);
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var construction = Construction.Create("Стройка", Guid.NewGuid());
        construction.SetProfileObject(id);
        db.Constructions.Add(construction);
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task Archive_LevelProfile_IsRefused_ButCanBeReturned()
    {
        var typeId = await SeedCompositeTypeAsync();
        var profile = await SeedProfileAsync(typeId);

        Assert.Equal(ArchiveOutcome.LevelProfile, await SetArchivedAsync(profile, true));
        Assert.Null(await ArchivedAtAsync(profile));

        // Если признак на профиле всё же оказался (прежние данные), снять его можно.
        var stuck = await SeedProfileAsync(typeId, ArchivedOn);
        Assert.Equal(ArchiveOutcome.Changed, await SetArchivedAsync(stuck, false));
    }

    /// <summary>
    /// Запись ушла в архив, копию сняли, запись вернули и сделали профилем. Копия поверх обязана
    /// оставить профиль действующим — и сказать об этом.
    /// </summary>
    [Fact]
    public async Task Restore_DoesNotArchiveLevelProfile_AndSaysSo()
    {
        var typeId = await SeedCompositeTypeAsync();
        var profile = await SeedProfileAsync(typeId);

        var report = await ImportManifestZipAsync(ArchiveManifest(knows: true, Entry(profile, typeId, ArchivedOn)));

        Assert.True(report.Success);
        Assert.Null(await ArchivedAtAsync(profile));
        Assert.Contains(report.Warnings, w => w.Contains("профилем"));
    }

    /// <summary>Дата со смещением (манифест собран снаружи) не роняет восстановление.</summary>
    [Fact]
    public async Task Restore_ArchiveDateWithOffset_IsStoredAsTheSameInstant()
    {
        var typeId = await SeedCompositeTypeAsync();
        var existing = await SeedRecordAsync(typeId);
        var brandNew = Guid.NewGuid();
        var local = ArchivedOn.ToOffset(TimeSpan.FromHours(3));

        var report = await ImportManifestZipAsync(ArchiveManifest(knows: true,
            Entry(existing, typeId, local), Entry(brandNew, typeId, local)));

        Assert.True(report.Success, string.Join("; ", report.Warnings));
        Assert.Equal(ArchivedOn, await ArchivedAtAsync(existing));
        Assert.Equal(ArchivedOn, await ArchivedAtAsync(brandNew));
    }

    /// <summary>
    /// Копия приносит ДОКУМЕНТ под идентификатором, под которым здесь лежит архивная запись.
    /// Документ в архиве не бывает: побеждает то, чем объект стал.
    /// </summary>
    [Fact]
    public async Task ClearOnDocuments_DropsArchiveFromObjectThatBecameDocument()
    {
        var id = await SeedRecordAsync(await SeedCompositeTypeAsync(), ArchivedOn);
        using (var scope = fixture.Services.CreateScope())
        {
            // Фасета добавлена мимо сущности — так, как это делает восстановление документа.
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO document_facets (\"ObjectId\", \"Status\", \"SortOrder\", \"PluginData\") " +
                "VALUES ({0}, 'Draft', 0, '{{}}'::jsonb)", id);
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IRecordArchive>().ClearOnDocumentsAsync());
        }

        Assert.Null(await ArchivedAtAsync(id));
    }

    [Fact]
    public async Task Archive_Document_IsNotArchived()
    {
        var typeId = await SeedCompositeTypeAsync();
        var id = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DomainObjects.Add(DomainObject.RestoreDocument(
                id, typeId, "Акт", JsonDocument.Parse("{}"), Guid.NewGuid(),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
                DocumentStatus.Draft, 0, null, null, null, JsonDocument.Parse("{}")));
            await db.SaveChangesAsync();
        }

        Assert.Equal(ArchiveOutcome.Document, await SetArchivedAsync(id, true));
        Assert.Null(await ArchivedAtAsync(id));
    }

    /// <summary>
    /// Главная причина, по которой колонку пишет отдельная служба: запись сохраняется целиком и без
    /// версии. Форма открыта → запись ушла в архив → форму сохранили. Архив обязан устоять.
    /// </summary>
    [Fact]
    public async Task Archive_SurvivesEditReadBeforeIt()
    {
        var id = await SeedRecordAsync(await SeedCompositeTypeAsync());

        using var scope = fixture.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDomainObjectRepository>();
        var read = await repo.GetByIdAsync(id);
        Assert.NotNull(read);
        Assert.False(read!.IsArchived);

        Assert.Equal(ArchiveOutcome.Changed, await SetArchivedAsync(id, true));

        read.Update("ООО Ромашка (переименована)", JsonDocument.Parse("""{"ИНН":"1"}"""));
        repo.Update(read);
        await repo.SaveChangesAsync();

        Assert.NotNull(await ArchivedAtAsync(id));
        using var check = fixture.Services.CreateScope();
        var saved = await check.ServiceProvider.GetRequiredService<AppDbContext>()
            .DomainObjects.AsNoTracking().SingleAsync(o => o.Id == id);
        Assert.Equal("ООО Ромашка (переименована)", saved.DisplayName);
    }

    [Fact]
    public async Task Export_CarriesArchiveAndSaysItKnowsIt()
    {
        var typeId = await SeedCompositeTypeAsync();
        var archived = await SeedRecordAsync(typeId);
        var live = await SeedRecordAsync(typeId);
        await SetArchivedAsync(archived, true);

        BackupManifest manifest;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zipStream, _) = await Backup(scope).ExportAsync(BackupScope.Full);
            await using var handle = zipStream;
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
            await using var entry = zip.GetEntry("manifest.json")!.Open();
            manifest = (await JsonSerializer.DeserializeAsync<BackupManifest>(entry))!;
        }

        Assert.True(manifest.KnowsRecordArchive);
        Assert.NotNull(manifest.CommonDataEntries.Single(e => e.Id == archived).ArchivedAt);
        Assert.Null(manifest.CommonDataEntries.Single(e => e.Id == live).ArchivedAt);
    }

    [Fact]
    public async Task Restore_KnowingCopy_SetsArchiveBothWays_OnExistingAndNewRecords()
    {
        var typeId = await SeedCompositeTypeAsync();
        var toArchive = await SeedRecordAsync(typeId);
        var toReturn = await SeedRecordAsync(typeId, ArchivedOn);
        var brandNew = Guid.NewGuid();

        var report = await ImportManifestZipAsync(ArchiveManifest(knows: true,
            Entry(toArchive, typeId, ArchivedOn),
            Entry(toReturn, typeId, null),
            Entry(brandNew, typeId, ArchivedOn)));

        Assert.True(report.Success);
        Assert.Equal(ArchivedOn, await ArchivedAtAsync(toArchive));
        Assert.Null(await ArchivedAtAsync(toReturn));
        Assert.Equal(ArchivedOn, await ArchivedAtAsync(brandNew));
    }

    /// <summary>
    /// Копия, снятая версией без признака: у каждой записи в ней null — и это «не знаю», а не «не в
    /// архиве». Применённое, оно вернуло бы в выбор всё, что убрали после снятия копии.
    /// </summary>
    [Fact]
    public async Task Restore_CopyWithoutArchiveKnowledge_LeavesArchiveAlone()
    {
        var typeId = await SeedCompositeTypeAsync();
        var archived = await SeedRecordAsync(typeId, ArchivedOn);
        var brandNew = Guid.NewGuid();

        var report = await ImportManifestZipAsync(ArchiveManifest(knows: null,
            Entry(archived, typeId, null),
            Entry(brandNew, typeId, null)));

        Assert.True(report.Success);
        Assert.Equal(ArchivedOn, await ArchivedAtAsync(archived));
        Assert.Null(await ArchivedAtAsync(brandNew));
    }
}
