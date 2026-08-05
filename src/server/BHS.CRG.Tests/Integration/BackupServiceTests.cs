using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Reconciliation;
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
        Assert.Equal(0, report.DataSetBindingTemplatesCreated);
        Assert.Equal(0, report.ReconciliationAliasesCreated);
    }

    /// <summary>
    /// Шаблон маппинга колонок — настройка типа документа, а не проектные данные: после
    /// восстановления типы и шаблоны возвращались, а стандартные маппинги к ним нет, и ничто об
    /// этом не сообщало (ревизия 2026-08-05).
    /// </summary>
    [Fact]
    public async Task Export_Import_RoundTrips_DataSetBindingTemplates()
    {
        var docTypeId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DocumentTypes.Add(DocumentType.Restore(
                docTypeId, "Кабельный журнал", "cable-journal", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            db.DataSetBindingTemplates.Add(DataSetBindingTemplate.Restore(
                templateId, docTypeId, "Стандартный кабельный", "Кабели",
                """{"Марка":"Тип кабеля","Длина":"L, м"}""", 3,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        byte[] zipBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zipStream, _) = await Backup(scope).ExportAsync();
            using var buf = new MemoryStream();
            await zipStream.CopyToAsync(buf);
            zipBytes = buf.ToArray();
        }

        await fixture.ResetDatabaseAsync();

        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await Backup(scope).ImportAsync(new MemoryStream(zipBytes));

        Assert.True(report.Success);
        Assert.Equal(1, report.DataSetBindingTemplatesCreated);

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var restored = await db.DataSetBindingTemplates.AsNoTracking().SingleAsync();
            Assert.Equal("Стандартный кабельный", restored.Name);
            Assert.Equal(docTypeId, restored.DocumentTypeId);
            Assert.Equal("Кабели", restored.TargetFieldKey);
            Assert.Contains("Тип кабеля", restored.ColumnMappings);
            Assert.Equal(3, restored.SortOrder);
        }
    }

    /// <summary>
    /// Алиасы — знание человека: пересчитать его нельзя, только надумать заново. Переносим РЕШЕНИЯ
    /// (подтверждённые и отклонённые) и НЕ переносим предложенные: это неразобранный шум, который на
    /// новой системе появится сам. Отклонённые важны не меньше подтверждённых — они и существуют
    /// затем, чтобы предложение не всплывало снова.
    /// </summary>
    [Fact]
    public async Task Export_Import_RoundTrips_ConfirmedAndRejectedAliases_ButNotProposed()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var confirmed = ReconciliationAlias.Propose("hyperline-cm1u", "Hyperline CM-1U-ML",
                "organizer", "Органайзер СвязьСтройДеталь", "одно и то же", "человек");
            confirmed.Review(AliasStatus.Confirmed, null, "человек");
            var rejected = ReconciliationAlias.Propose("kabel-vvg", "ВВГнг 3х1.5",
                "kabel-vvgng", "ВВГнг-LS 3х1.5", null, "агент");
            rejected.Review(AliasStatus.Rejected, "разные марки", "человек");
            var proposed = ReconciliationAlias.Propose("shkaf", "Шкаф 19\"",
                "shkaf-19", "Шкаф 19 дюймов", null, "агент");

            db.ReconciliationAliases.AddRange(confirmed, rejected, proposed);
            await db.SaveChangesAsync();
        }

        byte[] zipBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zipStream, _) = await Backup(scope).ExportAsync();
            using var buf = new MemoryStream();
            await zipStream.CopyToAsync(buf);
            zipBytes = buf.ToArray();
        }

        await fixture.ResetDatabaseAsync();

        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await Backup(scope).ImportAsync(new MemoryStream(zipBytes));

        Assert.True(report.Success);
        Assert.Equal(2, report.ReconciliationAliasesCreated);

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var restored = await db.ReconciliationAliases.AsNoTracking().ToListAsync();

            Assert.Equal(2, restored.Count);
            Assert.DoesNotContain(restored, a => a.Status == AliasStatus.Proposed);

            var confirmed = Assert.Single(restored, a => a.Status == AliasStatus.Confirmed);
            Assert.Equal("Hyperline CM-1U-ML", confirmed.AliasLabel);
            Assert.Equal("Органайзер СвязьСтройДеталь", confirmed.CanonicalLabel);
            Assert.Equal("человек", confirmed.ConfirmedBy);

            var rejected = Assert.Single(restored, a => a.Status == AliasStatus.Rejected);
            Assert.Equal("разные марки", rejected.Note);
        }
    }

    /// <summary>
    /// Запись общих данных, привязанная к комплекту, в чистой системе повисает: комплекты в копию
    /// не входят. Запись при этом СОХРАНЯЕМ — терять пользовательские данные хуже, — но отчёт обязан
    /// об этом сказать: иначе он называет успешно восстановленным то, чего в интерфейсе не видно.
    /// </summary>
    [Fact]
    public async Task Restore_CommonDataBoundToMissingSet_IsKeptButReported()
    {
        var compositeTypeId = Guid.NewGuid();
        var absentSetId = Guid.NewGuid();
        var entryId = Guid.NewGuid();

        var manifest = new BackupManifest(
            SchemaVersion: BackupService.CurrentSchemaVersion,
            AppVersion: BackupService.CurrentAppVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            DocumentTypes:
            [
                new BackupDocumentType(compositeTypeId, "Составной", $"c-{Guid.NewGuid():N}", "Composite", null, false,
                    JsonDocument.Parse("""{"fields":[]}""").RootElement.Clone(),
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
            Templates: [],
            CatalogEntities: [],
            CommonDataEntries:
            [
                new BackupCommonDataEntry(entryId, "Запись комплекта", compositeTypeId,
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    "Set", absentSetId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []),
            ]);

        var report = await ImportManifestAsync(manifest);

        Assert.True(report.Success);
        Assert.Equal(1, report.CommonDataEntriesCreated);   // запись НЕ потеряна
        Assert.Contains(report.Warnings, w => w.Contains("которых в этой системе нет"));

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull(await db.DomainObjects.AsNoTracking().FirstOrDefaultAsync(o => o.Id == entryId));
    }

    /// <summary>
    /// Ссылка на документ протухает молча: резолвер при генерации вернёт собственные данные объекта,
    /// без ошибки и без унаследованных полей. Дефект проявился бы в неверном PDF, далеко от
    /// восстановления, — поэтому о нём говорят здесь.
    /// </summary>
    [Fact]
    public async Task Restore_CommonDataReferencingDocument_IsReported()
    {
        var compositeTypeId = Guid.NewGuid();

        var manifest = new BackupManifest(
            SchemaVersion: BackupService.CurrentSchemaVersion,
            AppVersion: BackupService.CurrentAppVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            DocumentTypes:
            [
                new BackupDocumentType(compositeTypeId, "Составной", $"c-{Guid.NewGuid():N}", "Composite", null, false,
                    JsonDocument.Parse("""{"fields":[]}""").RootElement.Clone(),
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
            Templates: [],
            CatalogEntities: [],
            CommonDataEntries:
            [
                // Наследование от документа комплекта.
                new BackupCommonDataEntry(Guid.NewGuid(), "С наследованием", compositeTypeId,
                    JsonDocument.Parse("{\"_baseRef\":{\"kind\":\"instance\",\"id\":\"" + Guid.NewGuid() + "\"}}").RootElement.Clone(),
                    "System", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []),
                // Протягивание поля из реквизитов документа — на глубине, внутри массива.
                new BackupCommonDataEntry(Guid.NewGuid(), "Со ссылкой в массиве", compositeTypeId,
                    JsonDocument.Parse("{\"строки\":[{\"поле\":{\"$ref\":\"document\",\"instanceId\":\"" +
                                       Guid.NewGuid() + "\",\"fieldKey\":\"Номер\"}}]}").RootElement.Clone(),
                    "System", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []),
                // А эта ни на что не ссылается — в счёт попасть не должна.
                new BackupCommonDataEntry(Guid.NewGuid(), "Обычная", compositeTypeId,
                    JsonDocument.Parse("""{"Наименование":"Кабель"}""").RootElement.Clone(),
                    "System", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []),
            ]);

        var report = await ImportManifestAsync(manifest);

        Assert.True(report.Success);
        Assert.Contains(report.Warnings, w => w.Contains("2 записи ссылаются на документы"));
    }

    /// <summary>
    /// А если документ на месте — предупреждать не о чем. Это и есть самый обычный случай:
    /// восстановление в живую систему, где проектная работа никуда не девалась.
    ///
    /// Без проверки наличия предупреждение кричало бы на каждой унаследованной записи всегда.
    /// </summary>
    [Fact]
    public async Task Restore_CommonDataReferencingExistingDocument_IsSilent()
    {
        var compositeTypeId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        await fixture.ResetDatabaseAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DocumentTypes.Add(DocumentType.Restore(
                compositeTypeId, "Составной", $"c-{Guid.NewGuid():N}", DocumentTypeKind.Composite, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            // Адресат ссылки уже в системе.
            db.DomainObjects.Add(DomainObject.Restore(
                documentId, compositeTypeId, "Существующий", JsonDocument.Parse("{}"),
                CatalogScope.System, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var manifest = new BackupManifest(
            SchemaVersion: BackupService.CurrentSchemaVersion,
            AppVersion: BackupService.CurrentAppVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            DocumentTypes: [], Templates: [], CatalogEntities: [],
            CommonDataEntries:
            [
                new BackupCommonDataEntry(Guid.NewGuid(), "С наследованием", compositeTypeId,
                    JsonDocument.Parse("{\"_baseRef\":{\"kind\":\"instance\",\"id\":\"" + documentId + "\"}}").RootElement.Clone(),
                    "System", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []),
            ]);

        RestoreReport report;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = zip.CreateEntry("manifest.json");
                await using var w = entry.Open();
                await JsonSerializer.SerializeAsync(w, manifest, new JsonSerializerOptions { WriteIndented = true });
            }
            ms.Position = 0;
            using var scope = fixture.Services.CreateScope();
            report = await Backup(scope).ImportAsync(ms);
        }

        Assert.True(report.Success);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("ссылаются на документы"));
    }

    /// <summary>
    /// Данные записей — произвольный пользовательский JSON: ключ с именем <c>$ref</c> или
    /// <c>_baseRef.kind</c> нестрокового вида не должен ронять восстановление целиком.
    /// </summary>
    [Fact]
    public async Task Restore_CommonDataWithNonStringRefFields_DoesNotFail()
    {
        var compositeTypeId = Guid.NewGuid();

        var manifest = new BackupManifest(
            SchemaVersion: BackupService.CurrentSchemaVersion,
            AppVersion: BackupService.CurrentAppVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            DocumentTypes:
            [
                new BackupDocumentType(compositeTypeId, "Составной", $"c-{Guid.NewGuid():N}", "Composite", null, false,
                    JsonDocument.Parse("""{"fields":[]}""").RootElement.Clone(),
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
            Templates: [], CatalogEntities: [],
            CommonDataEntries:
            [
                new BackupCommonDataEntry(Guid.NewGuid(), "Странная", compositeTypeId,
                    JsonDocument.Parse("""{"$ref":42,"_baseRef":{"kind":1},"вложенное":{"$ref":null}}""").RootElement.Clone(),
                    "System", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []),
            ]);

        var report = await ImportManifestAsync(manifest);

        Assert.True(report.Success, string.Join(" | ", report.Warnings));
        Assert.Equal(1, report.CommonDataEntriesCreated);
    }

    /// <summary>
    /// Версия в манифесте — то, ради чего поле существует: «какой сборкой снята копия». Константа в
    /// коде делала все копии одинаковыми независимо от сборки.
    /// </summary>
    [Fact]
    public void Manifest_AppVersion_MatchesAssemblyVersion()
    {
        // Сверяем с ДРУГОЙ сборкой решения: версия у всех проектов общая (Directory.Build.props),
        // поэтому совпадение здесь означает «взято из сборки», а не «сравнили значение с собой».
        var solutionVersion = typeof(BackupManifest).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+', 2)[0];

        // Проверка «не 1.0.0» тут была бы миной: 1.0.0 — это заявленный первый релиз, и в день его
        // выпуска тест покраснел бы на ровном месте. Совпадение с версией сборки и так доказывает,
        // что значение берётся из сборки, а не написано в коде.
        Assert.Equal(solutionVersion, BackupService.CurrentAppVersion);
    }

    /// <summary>Собирает zip из манифеста и восстанавливает — без блобов.</summary>
    private async Task<RestoreReport> ImportManifestAsync(BackupManifest manifest)
    {
        await fixture.ResetDatabaseAsync();

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("manifest.json");
            await using var w = entry.Open();
            await JsonSerializer.SerializeAsync(w, manifest, new JsonSerializerOptions { WriteIndented = true });
        }
        ms.Position = 0;

        using var scope = fixture.Services.CreateScope();
        return await Backup(scope).ImportAsync(ms);
    }

    /// <summary>
    /// Тождество алиаса — КЛЮЧ, а не идентификатор: на нём уникальный индекс, и так же считает путь
    /// записи в приложении. Сценарий обыденный: на целевой системе то же предложение родилось
    /// заново, с другим Id, но с тем же ключом, — а копия несёт решение человека по этому ключу.
    ///
    /// Восстановление идёт ОДНОЙ транзакцией, поэтому вставка, упавшая на уникальном индексе,
    /// откатила бы вместе с алиасами и типы, и шаблоны, и каталог.
    /// </summary>
    [Fact]
    public async Task Import_AliasWithSameKeyButDifferentId_ReplacesInsteadOfFailing()
    {
        const string key = "hyperline-cm1u";

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var a = ReconciliationAlias.Propose(key, "Hyperline CM-1U-ML", "organizer", "Органайзер", null, "человек");
            a.Review(AliasStatus.Confirmed, "из копии", "человек");
            db.ReconciliationAliases.Add(a);
            db.DocumentTypes.Add(DocumentType.Restore(
                Guid.NewGuid(), "Тип", "type-x", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            await db.SaveChangesAsync();
        }

        byte[] zipBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zipStream, _) = await Backup(scope).ExportAsync();
            using var buf = new MemoryStream();
            await zipStream.CopyToAsync(buf);
            zipBytes = buf.ToArray();
        }

        // На целевой системе тот же ключ, но запись другая: другой Id, другое решение.
        await fixture.ResetDatabaseAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var local = ReconciliationAlias.Propose(key, "Hyperline CM-1U-ML", "other", "Другой канон", null, "агент");
            local.Review(AliasStatus.Rejected, "местное решение", "местный");
            db.ReconciliationAliases.Add(local);
            await db.SaveChangesAsync();
        }

        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await Backup(scope).ImportAsync(new MemoryStream(zipBytes));

        // Восстановление НЕ падает целиком, и типы документов из той же копии доезжают.
        Assert.True(report.Success, string.Join(" | ", report.Warnings));
        Assert.Equal(1, report.ReconciliationAliasesUpdated);
        Assert.Equal(0, report.ReconciliationAliasesCreated);
        Assert.Equal(1, report.DocumentTypesCreated);

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var alias = Assert.Single(await db.ReconciliationAliases.AsNoTracking().ToListAsync());
            Assert.Equal(AliasStatus.Confirmed, alias.Status);   // выиграла копия
            Assert.Equal("из копии", alias.Note);
        }
    }
}
