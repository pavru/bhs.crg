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
            await using var _zipHandle = zip;
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
            await using var _zipHandle = zip;
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
            await using var _zipHandle = zipStream;
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
            await using var _zipHandle = zipStream;
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

    /// <summary>
    /// Рецепт обработки источника (issue #687) — конфигурация без единой внешней ссылки: внутри
    /// только имя и правила, адресующие колонки по именам. Исключение подсистемы наборов данных из
    /// копии (#403) касалось проектного сырья и крупных блобов, а не переиспользуемых рецептов.
    /// </summary>
    [Fact]
    public async Task Export_Import_RoundTrips_DataSetProcessingTemplates()
    {
        var templateId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DataSetProcessingTemplates.Add(DataSetProcessingTemplate.Restore(
                templateId, "Кабели без резерва", "Лист1", """[{"alias":"Марка","expr":"./td[1]"}]""",
                """{"logic":"and","conditions":[{"column":"Тип","op":"ne","value":"резерв"}]}""",
                """[{"alias":"Итого","expr":"row['Длина'] * 1.05"}]""",
                """[{"column":"Марка","direction":"asc"}]""",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        byte[] zipBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zipStream, _) = await Backup(scope).ExportAsync();
            await using var _zipHandle = zipStream;
            using var buf = new MemoryStream();
            await zipStream.CopyToAsync(buf);
            zipBytes = buf.ToArray();
        }

        await fixture.ResetDatabaseAsync();

        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await Backup(scope).ImportAsync(new MemoryStream(zipBytes));

        Assert.True(report.Success);
        Assert.Equal(1, report.DataSetProcessingTemplatesCreated);

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var restored = await db.DataSetProcessingTemplates.AsNoTracking().SingleAsync();
            Assert.Equal(templateId, restored.Id);
            Assert.Equal("Кабели без резерва", restored.Name);
            Assert.Equal("Лист1", restored.SheetOrPath);
            Assert.Contains("резерв", restored.RowFilter);
            Assert.Contains("Итого", restored.ComputedColumns);
            Assert.Contains("asc", restored.SortSpec);
        }
    }

    /// <summary>
    /// Библиотека документов качества со сканами (issue #687). Скан обязан ехать в архиве: без него
    /// восстановленный сертификат ничего не подтверждает — это сам документ, а не иллюстрация к нему.
    /// </summary>
    [Fact]
    public async Task Export_Import_RoundTrips_QualityDocumentsWithScans()
    {
        const string scanPath = "quality/2026/certificate.pdf";
        byte[] scanBytes = [37, 80, 68, 70, 45];
        var docTypeId = Guid.NewGuid();
        var qualityId = Guid.NewGuid();

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            db.DocumentTypes.Add(DocumentType.Restore(
                docTypeId, "Сертификат соответствия", "cert", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));

            await blob.PutAsync(scanPath, new MemoryStream(scanBytes), "application/pdf", default);
            db.QualityDocuments.Add(QualityDocument.Restore(
                qualityId, docTypeId, "ЕАЭС RU С-RU.АТ21.В.00157", JsonDocument.Parse("""{"Номер":"00157"}"""),
                CatalogScope.System, null, QualityDocSource.Web, "https://example.test/cert.pdf",
                scanPath, "certificate.pdf", "application/pdf",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        byte[] zipBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zipStream, _) = await Backup(scope).ExportAsync();
            await using var _zipHandle = zipStream;
            using var buf = new MemoryStream();
            await zipStream.CopyToAsync(buf);
            zipBytes = buf.ToArray();
        }

        using (var check = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
            Assert.Contains(check.Entries, e => e.FullName == $"blobs/{scanPath}");

        // Чистое окружение: ни записи, ни файла.
        using (var scope = fixture.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IBlobStorage>().DeleteAsync(scanPath);
        await fixture.ResetDatabaseAsync();

        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await Backup(scope).ImportAsync(new MemoryStream(zipBytes));

        Assert.True(report.Success);
        Assert.Equal(1, report.QualityDocumentsCreated);

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            var restored = await db.QualityDocuments.AsNoTracking().SingleAsync();
            Assert.Equal(qualityId, restored.Id);
            Assert.Equal(docTypeId, restored.DocumentTypeId);
            Assert.Equal(QualityDocSource.Web, restored.Source);
            Assert.Equal("https://example.test/cert.pdf", restored.SourceUrl);
            Assert.Equal(CatalogScope.System, restored.Scope);
            Assert.Equal("certificate.pdf", restored.ScanFileName);
            Assert.Contains("00157", restored.Requisites.RootElement.GetRawText());

            await using var stream = await blob.DownloadAsync(scanPath);
            using var buf = new MemoryStream();
            await stream.CopyToAsync(buf);
            Assert.Equal(scanBytes, buf.ToArray());
        }
    }

    /// <summary>
    /// Документ качества уровня комплекта в чистой системе повисает — комплектов в копии нет. Как и
    /// общие данные, его СОХРАНЯЕМ: сертификат остаётся сертификатом. Но отчёт обязан сказать, что
    /// в библиотеке его не увидят.
    /// </summary>
    [Fact]
    public async Task Restore_QualityDocumentBoundToMissingSet_IsKeptButReported()
    {
        var docTypeId = Guid.NewGuid();
        var qualityId = Guid.NewGuid();
        var absentSetId = Guid.NewGuid();

        var manifest = ManifestWith(
            documentTypes:
            [
                new BackupDocumentType(docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", "Document", null, false,
                    JsonDocument.Parse("""{"fields":[]}""").RootElement.Clone(),
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
            qualityDocuments:
            [
                new BackupQualityDocument(qualityId, docTypeId, "Паспорт кабеля",
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    nameof(CatalogScope.Set), absentSetId, nameof(QualityDocSource.Manual), null,
                    null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ]);

        var report = await ImportManifestAsync(manifest);

        Assert.True(report.Success);
        Assert.Equal(1, report.QualityDocumentsCreated);
        Assert.Contains(report.Warnings, w => w.Contains("комплектам, разделам или стройкам") && w.Contains("Документы качества"));

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull(await db.QualityDocuments.AsNoTracking().FirstOrDefaultAsync(q => q.Id == qualityId));
    }

    /// <summary>
    /// Скан не доехал (в хранилище источника его уже не было — экспорт пропускает такой файл с
    /// записью в лог). Документ восстанавливается, но отчёт обязан назвать это прямо: иначе он
    /// сообщает об успехе там, где карточка есть, а подтверждать ей нечем.
    /// </summary>
    [Fact]
    public async Task Restore_QualityDocumentWhoseScanIsMissingFromArchive_IsReported()
    {
        var docTypeId = Guid.NewGuid();

        var manifest = ManifestWith(
            documentTypes:
            [
                new BackupDocumentType(docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", "Document", null, false,
                    JsonDocument.Parse("""{"fields":[]}""").RootElement.Clone(),
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
            qualityDocuments:
            [
                new BackupQualityDocument(Guid.NewGuid(), docTypeId, "Сертификат без скана",
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    nameof(CatalogScope.System), null, nameof(QualityDocSource.Manual), null,
                    "quality/lost.pdf", "lost.pdf", "application/pdf",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ]);

        var report = await ImportManifestAsync(manifest);

        Assert.True(report.Success);
        Assert.Equal(1, report.QualityDocumentsCreated);
        Assert.Contains(report.Warnings, w => w.Contains("скан не восстановлен"));
    }

    /// <summary>
    /// Восстановление ничего не удаляет, и на самом обычном пути — админ накатывает копию на живую
    /// систему, чтобы вернуть шаблон, — связки с материалами остаются на месте. Безусловное
    /// «библиотека вернулась непривязанной» объявило бы там потерянной целую работу и позвало бы
    /// делать её заново.
    /// </summary>
    [Fact]
    public async Task Restore_WhenMaterialLinksSurvive_DoesNotClaimLibraryCameBackUnlinked()
    {
        var docTypeId = Guid.NewGuid();
        var liveDocId = Guid.NewGuid();

        await fixture.ResetDatabaseAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DocumentTypes.Add(DocumentType.Restore(
                docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            db.QualityDocuments.Add(QualityDocument.Restore(
                liveDocId, docTypeId, "Живой сертификат", JsonDocument.Parse("{}"),
                CatalogScope.System, null, QualityDocSource.Manual, null, null, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            db.MaterialQualityLinks.Add(MaterialQualityLink.Create(
                CatalogScope.System, null, "vvgng-3x1.5", liveDocId, "ВВГнг 3х1.5"));
            await db.SaveChangesAsync();
        }

        var report = await ImportManifestZipAsync(ManifestWith(
            documentTypes:
            [
                new BackupDocumentType(docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", "Document", null, false,
                    JsonDocument.Parse("""{"fields":[]}""").RootElement.Clone(),
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
            qualityDocuments:
            [
                new BackupQualityDocument(Guid.NewGuid(), docTypeId, "Новый из копии",
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    nameof(CatalogScope.System), null, nameof(QualityDocSource.Manual), null,
                    null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ]));

        Assert.True(report.Success);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("непривязанной"));
    }

    /// <summary>
    /// Скан загрузили уже ПОСЛЕ снятия копии: восстановление снимет указатель на него, и обещание
    /// «добавляет и обновляет, но ничего не удаляет» тут перестаёт быть правдой. Данные всё равно
    /// берём из копии, но сказать об этом обязаны — по смыслу библиотеки скан и есть документ.
    /// </summary>
    [Fact]
    public async Task Restore_ScanUploadedAfterBackupWasTaken_IsReported()
    {
        var docTypeId = Guid.NewGuid();
        var docId = Guid.NewGuid();

        await fixture.ResetDatabaseAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DocumentTypes.Add(DocumentType.Restore(
                docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            db.QualityDocuments.Add(QualityDocument.Restore(
                docId, docTypeId, "Сертификат со сканом", JsonDocument.Parse("{}"),
                CatalogScope.System, null, QualityDocSource.Manual, null,
                "quality/added-later.pdf", "added-later.pdf", "application/pdf",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        // Копия несёт ту же карточку, но снятую ДО загрузки скана.
        var report = await ImportManifestZipAsync(ManifestWith(
            documentTypes:
            [
                new BackupDocumentType(docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", "Document", null, false,
                    JsonDocument.Parse("""{"fields":[]}""").RootElement.Clone(),
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
            qualityDocuments:
            [
                new BackupQualityDocument(docId, docTypeId, "Сертификат со сканом",
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    nameof(CatalogScope.System), null, nameof(QualityDocSource.Manual), null,
                    null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ]));

        Assert.True(report.Success);
        Assert.Equal(1, report.QualityDocumentsUpdated);
        Assert.Contains(report.Warnings, w => w.Contains("скан был в этой системе"));
        // Родительный падеж после «у»: именительный дал бы «у 1 запись».
        Assert.Contains(report.Warnings, w => w.Contains("у 1 записи"));
    }

    /// <summary>
    /// Имя документа качества уникально в своей области (issue #588), но восстановление — путь
    /// записи мимо этой проверки. Те же сертификаты, успевшие появиться руками, дают в списке
    /// неразличимые пары; отказывать нельзя (откатится вся транзакция), значит надо сказать.
    /// </summary>
    [Fact]
    public async Task Restore_QualityDocumentDuplicatingNameInSameScope_IsReported()
    {
        var docTypeId = Guid.NewGuid();

        await fixture.ResetDatabaseAsync();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DocumentTypes.Add(DocumentType.Restore(
                docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            db.QualityDocuments.Add(QualityDocument.Restore(
                Guid.NewGuid(), docTypeId, "EKF — автоматические выключатели", JsonDocument.Parse("{}"),
                CatalogScope.System, null, QualityDocSource.Manual, null, null, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var report = await ImportManifestZipAsync(ManifestWith(
            documentTypes:
            [
                new BackupDocumentType(docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", "Document", null, false,
                    JsonDocument.Parse("""{"fields":[]}""").RootElement.Clone(),
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ],
            qualityDocuments:
            [
                // Другой идентификатор, то же имя в той же области — ровно случай #588.
                new BackupQualityDocument(Guid.NewGuid(), docTypeId, "EKF — автоматические выключатели",
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    nameof(CatalogScope.System), null, nameof(QualityDocSource.Manual), null,
                    null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            ]));

        Assert.True(report.Success);
        Assert.Contains(report.Warnings, w => w.Contains("совпадает по имени с уже заведёнными"));
    }

    /// <summary>Манифест с одними нужными секциями — остальные пустые.</summary>
    private static BackupManifest ManifestWith(
        BackupDocumentType[] documentTypes,
        BackupQualityDocument[]? qualityDocuments = null) =>
        new(SchemaVersion: BackupService.CurrentSchemaVersion,
            AppVersion: BackupService.CurrentAppVersion,
            CreatedAt: DateTimeOffset.UtcNow,
            DocumentTypes: documentTypes,
            Templates: [],
            CatalogEntities: [],
            CommonDataEntries: [],
            QualityDocuments: qualityDocuments);

    /// <summary>Собирает zip из манифеста и восстанавливает — без блобов.</summary>
    private async Task<RestoreReport> ImportManifestAsync(BackupManifest manifest)
    {
        await fixture.ResetDatabaseAsync();
        return await ImportManifestZipAsync(manifest);
    }

    /// <summary>
    /// То же, но БЕЗ очистки: для случаев, где проверяется встреча копии с уже существующими
    /// данными (связки, скан, дубль имени) — очистка снесла бы то самое, ради чего тест написан.
    /// </summary>
    private async Task<RestoreReport> ImportManifestZipAsync(BackupManifest manifest)
    {
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
            await using var _zipHandle = zipStream;
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

    /// <summary>
    /// Оценка веса копии сходится с настоящим архивом (issue #711).
    ///
    /// Это главная проверка новой оценки, и сверяется она не с ожидаемым числом, а с ФАКТОМ:
    /// снимаем копию тех же данных и сравниваем длины. Число, посчитанное по своей же формуле,
    /// доказывало бы только то, что формула не менялась, — а сходиться она обязана с zip.
    ///
    /// Скан берём заведомо несжимаемый (псевдослучайные байты): именно так ведут себя сканы в
    /// PDF, и именно поэтому они кладутся в архив без сжатия. Данные, которые сжимаются в ноль,
    /// скрыли бы ошибку в учёте того, что сжимается, а что нет.
    /// </summary>
    [Fact]
    public async Task EstimateSize_MatchesActualArchive()
    {
        const string scanPath = "quality/2026/big-certificate.pdf";
        var scanBytes = new byte[256 * 1024];
        new Random(711).NextBytes(scanBytes);
        var docTypeId = Guid.NewGuid();

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            db.DocumentTypes.Add(DocumentType.Restore(
                docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));

            await blob.PutAsync(AssetBlobPath, new MemoryStream(AssetBytes), "image/png", default);
            db.TemplateAssets.Add(TemplateAsset.Restore(Guid.NewGuid(), TemplateAssetScope.System, null,
                TemplateAssetKind.Image, "logo", "logo.png", "image/png", AssetBlobPath, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

            await blob.PutAsync(scanPath, new MemoryStream(scanBytes), "application/pdf", default);
            db.QualityDocuments.Add(QualityDocument.Restore(
                Guid.NewGuid(), docTypeId, "ЕАЭС RU С-RU.АТ21.В.00157", JsonDocument.Parse("""{"Номер":"00157"}"""),
                CatalogScope.System, null, QualityDocSource.Web, null,
                scanPath, "big-certificate.pdf", "application/pdf",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        BackupSizeEstimate estimate;
        long actualBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var svc = Backup(scope);
            estimate = await svc.EstimateSizeAsync(limitBytes: 500L * 1024 * 1024);

            var (zipStream, _) = await svc.ExportAsync();
            await using var _zipHandle = zipStream;
            actualBytes = zipStream.Length;
        }

        // Сверяем КОНФИГУРАЦИОННЫЙ состав: именно его и снял ExportAsync выше (issue #833).
        var config = estimate.Configuration;
        Assert.Equal(2, config.BlobCount);
        Assert.Equal(0, config.MissingBlobCount);
        // Блобы лежат в архиве как есть — их вклад точен, а не приближён.
        Assert.Equal(scanBytes.LongLength + AssetBytes.LongLength, config.BlobBytes);
        Assert.False(config.TotalBytes > estimate.LimitBytes);

        // Расхождение — считаные байты: заголовки zip считаются по длине имени, а не круглой
        // константой (круглая занижала оценку на рабочей базе почти на 6 КБ).
        var diff = Math.Abs(config.TotalBytes - actualBytes);
        Assert.True(diff < 256,
            $"оценка {config.TotalBytes} против архива {actualBytes} (разница {diff} байт)");
    }

    // ── Проектные данные (issue #833) ─────────────────────────────────────────

    /// <summary>
    /// Полная копия переносит проектную работу: стройку с разделом и комплектом, документ комплекта
    /// со статусом и выпущенным файлом, набор данных с разобранным источником и привязкой.
    ///
    /// Ради этого issue и заведён: переезд dev → рабочий сервер не восстановил ни одной стройки, а
    /// записи общих данных «относились к стройкам, которых нет». Проверяем именно переезд: снять,
    /// стереть всё, восстановить — и увидеть работающую систему, а не набор карточек.
    /// </summary>
    [Fact]
    public async Task FullBackup_RoundTrips_ProjectData()
    {
        var typeId = Guid.NewGuid();
        var constructionId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var setId = Guid.NewGuid();
        var docId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        const string pdfPath = "generated/act-1.pdf";
        const string rawPath = "datasets/kabelnyy-zhurnal.xlsx";

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();

            db.DocumentTypes.Add(DocumentType.Restore(
                typeId, "АОСР", $"aosr-{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));

            db.Constructions.Add(Construction.Restore(constructionId, "ЖК Северный", Guid.NewGuid(), null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            db.Sections.Add(Section.Restore(sectionId, constructionId, "ЭОМ", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            db.DocumentSets.Add(DocumentSet.Restore(setId, sectionId, "Комплект 1", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

            var doc = DomainObject.RestoreDocument(
                docId, typeId, "АОСР № 1", JsonDocument.Parse("""{"Номер":"1"}"""), setId,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ["акт номер один"],
                DocumentStatus.Generated, sortOrder: 3, templateId: null, templateIds: null,
                templateParams: null, pluginData: JsonDocument.Parse("{}"));
            db.DomainObjects.Add(doc);
            await db.SaveChangesAsync();

            await blob.PutAsync(pdfPath, new MemoryStream([9, 9, 9]), "application/pdf", default);
            db.GeneratedFiles.Add(GeneratedFile.Restore(Guid.NewGuid(), docId, OutputFormat.Pdf, pdfPath,
                null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

            await blob.PutAsync(rawPath, new MemoryStream([7, 7]), "application/octet-stream", default);
            db.DataSetFiles.Add(DataSetFile.Restore(fileId, "Кабельный журнал", DataSetFormat.Xlsx, rawPath,
                CatalogScope.Set, setId, null, null, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            db.DataSetSources.Add(DataSetSource.Restore(sourceId, fileId, "Лист1", "Лист1", null,
                """[{"name":"Марка"}]""", 2, """[{"Марка":"ВВГнг"}]""", null, null, null, null,
                null, null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();

            db.DataSetBindings.Add(DataSetBinding.Restore(Guid.NewGuid(), docId, sourceId, "таблица",
                """{"Марка":"Марка"}""", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        // ── Снимаем ПОЛНУЮ копию ──────────────────────────────────────────────
        byte[] zipBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zip, _) = await Backup(scope).ExportAsync(BackupScope.Full);
            await using var _zipHandle = zip;
            using var ms = new MemoryStream();
            await zip.CopyToAsync(ms);
            zipBytes = ms.ToArray();
        }

        // Сырьё наборов и выпущенные PDF обязаны лежать в архиве: без них восстановленная система
        // покажет карточки без содержимого.
        using (var check = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
        {
            Assert.Contains(check.Entries, e => e.FullName == $"blobs/{pdfPath}");
            Assert.Contains(check.Entries, e => e.FullName == $"blobs/{rawPath}");
        }

        // ── Чистая установка ──────────────────────────────────────────────────
        using (var scope = fixture.Services.CreateScope())
        {
            var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
            await blob.DeleteAsync(pdfPath);
            await blob.DeleteAsync(rawPath);
        }
        await fixture.ResetDatabaseAsync();

        // ── Восстанавливаем ───────────────────────────────────────────────────
        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await Backup(scope).ImportAsync(new MemoryStream(zipBytes));

        Assert.True(report.Success, string.Join("; ", report.Warnings));

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Assert.Equal("ЖК Северный", (await db.Constructions.FindAsync(constructionId))!.Name);
            Assert.Equal(constructionId, (await db.Sections.FindAsync(sectionId))!.ConstructionId);
            Assert.Equal(sectionId, (await db.DocumentSets.FindAsync(setId))!.SectionId);

            // Документ — именно документ: фасета на месте, со статусом и порядком.
            var doc = await db.DomainObjects.Include(o => o.Facet).FirstAsync(o => o.Id == docId);
            Assert.NotNull(doc.Facet);
            Assert.Equal(DocumentStatus.Generated, doc.Facet!.Status);
            Assert.Equal(3, doc.Facet.SortOrder);
            Assert.Equal(setId, doc.ScopeId);
            Assert.Equal(["акт номер один"], doc.Aliases);

            Assert.Equal(pdfPath, (await db.GeneratedFiles.FirstAsync(f => f.ObjectId == docId)).BlobPath);

            // Источник восстановлен С КЭШЕМ: без него набор приехал бы пустым — файл есть, строк нет.
            var source = await db.DataSetSources.FirstAsync(x => x.Id == sourceId);
            Assert.Equal(2, source.CachedRowCount);
            Assert.Contains("ВВГнг", source.CachedData);
            Assert.Equal(fileId, source.FileId);

            var binding = await db.DataSetBindings.FirstAsync(b => b.OwnerId == docId);
            Assert.Equal(sourceId, binding.SourceId);

            // Файлы вернулись в хранилище — иначе PDF документа и сырьё набора были бы битыми ссылками.
            var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
            Assert.NotNull(await blob.GetSizeAsync(pdfPath));
            Assert.NotNull(await blob.GetSizeAsync(rawPath));
        }
    }

    /// <summary>
    /// Конфигурационная копия проектных данных НЕ несёт — она осталась ровно тем, чем была.
    /// Проверяем негативом: иначе выбор состава был бы украшением, а установка, которой нужна
    /// лёгкая копия, молча получала бы гигабайты.
    /// </summary>
    [Fact]
    public async Task ConfigurationBackup_LeavesProjectDataOut()
    {
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Constructions.Add(Construction.Restore(Guid.NewGuid(), "ЖК Южный", Guid.NewGuid(), null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        byte[] zipBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zip, _) = await Backup(scope).ExportAsync(BackupScope.Configuration);
            await using var _zipHandle = zip;
            using var ms = new MemoryStream();
            await zip.CopyToAsync(ms);
            zipBytes = ms.ToArray();
        }

        await fixture.ResetDatabaseAsync();
        using (var scope = fixture.Services.CreateScope())
            await Backup(scope).ImportAsync(new MemoryStream(zipBytes));

        using (var scope = fixture.Services.CreateScope())
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Constructions.ToListAsync());
    }

    /// <summary>
    /// Копия, снятая ДО issue #833, восстанавливается как прежде: новых секций в ней нет, и
    /// отсутствие их — не отказ. Ради этого секции и добавлены аддитивно, без смены версии схемы.
    /// </summary>
    [Fact]
    public async Task OldBackupWithoutProjectSections_StillRestores()
    {
        var typeId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DocumentTypes.Add(DocumentType.Restore(
                typeId, "Старый тип", $"old-{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            await db.SaveChangesAsync();
        }

        // Архив без единой новой секции — ровно то, что писала версия до #833.
        byte[] zipBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zip, _) = await Backup(scope).ExportAsync();
            await using var _zipHandle = zip;
            using var ms = new MemoryStream();
            await zip.CopyToAsync(ms);
            zipBytes = ms.ToArray();
        }
        using (var check = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
        {
            using var reader = new StreamReader(check.GetEntry("manifest.json")!.Open());
            var json = await reader.ReadToEndAsync();
            Assert.Contains("\"Constructions\": null", json);
        }

        await fixture.ResetDatabaseAsync();
        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await Backup(scope).ImportAsync(new MemoryStream(zipBytes));

        Assert.True(report.Success);
        Assert.Null(report.ProjectSections);
        Assert.Equal(1, report.DocumentTypesCreated);
    }

    /// <summary>
    /// Оценка не завышает на битых ссылках. Экспорт недоступный блоб пропускает с предупреждением —
    /// значит и веса он не добавляет; но молчать о нём тоже нельзя: битая ссылка иначе не всплывёт
    /// нигде, кроме лога экспорта.
    /// </summary>
    [Fact]
    public async Task EstimateSize_CountsMissingBlobsSeparately_AndDoesNotChargeForThem()
    {
        var docTypeId = Guid.NewGuid();
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DocumentTypes.Add(DocumentType.Restore(
                docTypeId, "Сертификат", $"cert-{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            db.QualityDocuments.Add(QualityDocument.Restore(
                Guid.NewGuid(), docTypeId, "Сертификат без файла", JsonDocument.Parse("{}"),
                CatalogScope.System, null, QualityDocSource.Manual, null,
                "quality/2026/pointer-to-nowhere.pdf", "нет.pdf", "application/pdf",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        using var estimateScope = fixture.Services.CreateScope();
        var estimate = await Backup(estimateScope).EstimateSizeAsync(limitBytes: 500L * 1024 * 1024);

        Assert.Equal(1, estimate.Configuration.BlobCount);
        Assert.Equal(1, estimate.Configuration.MissingBlobCount);
        Assert.Equal(0, estimate.Configuration.BlobBytes);
    }

    /// <summary>
    /// Предел — не украшение: копия сверх него помечена как непринимаемая. Проверяем негативом,
    /// подставив предел ниже фактического веса, — иначе признак остался бы вычислением, которое
    /// никогда не срабатывало.
    /// </summary>
    [Fact]
    public async Task EstimateSize_MarksCopyThatWouldBeRejected()
    {
        using var scope = fixture.Services.CreateScope();
        var estimate = await Backup(scope).EstimateSizeAsync(limitBytes: 1);

        Assert.True(estimate.Configuration.TotalBytes > 1);
        Assert.True(estimate.Configuration.TotalBytes > estimate.LimitBytes);
    }
}
