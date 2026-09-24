using System.IO.Compression;
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

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Круговой прогон копии — часть <see cref="BackupServiceTests" />: выгрузили, стёрли,
/// восстановили, сошлось.
///
/// <para>Здесь проверяется ПОЛНОТА: попала ли сущность в архив и вернулась ли целой. Что копия
/// СООБЩАЕТ о неполных данных — отдельный файл, <c>BackupServiceTests.RestoreReport.cs</c>:
/// вопросы разные, и правка здесь не отвечает на тамошний.</para>
///
/// <para>Тесты проектной работы (issue #833) живут тут же: переезд dev → рабочий сервер и есть
/// круговой прогон, только целиком. Прежний разделитель «── Проектные данные ──» не перенесён
/// намеренно: он размечал положение в файле, а тесты #833 теперь лежат в двух файлах — и на новом
/// месте эта разметка обещала бы границу, которой нет.</para>
/// </summary>
public partial class BackupServiceTests
{
    /// <summary>
    /// Полнота бэкапа (issue #403): конфигурация, от которой зависит генерация — переиспользуемые
    /// перечисления (EnumType), ассеты шаблонов (TemplateAsset + их блобы) и общая Typst-библиотека
    /// (TypstUserLib, синглтон) — должна пережить export→wipe→import. До #403 эти три сущности в
    /// бэкап не попадали вовсе.
    ///
    /// <para>Текст стоял доккомментарием КЛАССА, хотя описывал ровно этот тест и три его сущности:
    /// у самого теста своего описания не было. При разрезе вернул его владельцу.</para>
    /// </summary>
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
    /// Восстановление НА ЖИВУЮ систему, где тот же материал уже связан своей строкой.
    ///
    /// У связок есть уникальный индекс по (уровень, носитель, ключ материала), а идентификаторы у
    /// двух систем свои. Раскладывай мы связки по одному лишь Id — вставка упёрлась бы в него, и
    /// это не «пропустим одну строку»: 23505 откатывает ВСЮ транзакцию восстановления, то есть
    /// администратор получает пустую систему и сообщение про нарушение ограничения. Проверяем
    /// именно этот путь: копия одной установки поверх работающей другой.
    /// </summary>
    [Fact]
    public async Task FullBackup_RestoresOntoLiveSystem_WithSameMaterialLinkedById()
    {
        var typeId = Guid.NewGuid();
        var constructionId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var setId = Guid.NewGuid();
        var qualityDocId = Guid.NewGuid();
        var sourceLinkId = Guid.NewGuid();
        const string materialKey = "кабель ВВГнг 3х2,5";

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.DocumentTypes.Add(DocumentType.Restore(
                typeId, "Сертификат", $"cert-{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
                JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"),
                false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false));
            db.Constructions.Add(Construction.Restore(constructionId, "Стройка", Guid.NewGuid(), null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            db.Sections.Add(Section.Restore(sectionId, constructionId, "Раздел", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            db.DocumentSets.Add(DocumentSet.Restore(setId, sectionId, "Комплект", null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            db.QualityDocuments.Add(QualityDocument.Restore(
                qualityDocId, typeId, "Сертификат на кабель", JsonDocument.Parse("{}"),
                CatalogScope.System, null, QualityDocSource.Manual, null, null, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();

            db.MaterialQualityLinks.Add(MaterialQualityLink.Restore(
                sourceLinkId, CatalogScope.Set, setId, materialKey, "Кабель", qualityDocId,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        byte[] zipBytes;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zip, _) = await Backup(scope).ExportAsync(BackupScope.Full);
            await using var _zipHandle = zip;
            using var ms = new MemoryStream();
            await zip.CopyToAsync(ms);
            zipBytes = ms.ToArray();
        }

        // Целевая система: тот же материал в том же комплекте связан ДРУГОЙ строкой — так и
        // выглядит установка, которая жила своей жизнью.
        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MaterialQualityLinks.RemoveRange(db.MaterialQualityLinks);
            await db.SaveChangesAsync();
            db.MaterialQualityLinks.Add(MaterialQualityLink.Restore(
                Guid.NewGuid(), CatalogScope.Set, setId, materialKey, "Кабель (местная связка)",
                qualityDocId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        RestoreReport report;
        using (var scope = fixture.Services.CreateScope())
            report = await Backup(scope).ImportAsync(new MemoryStream(zipBytes));

        Assert.True(report.Success, string.Join("; ", report.Warnings));

        using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var links = await db.MaterialQualityLinks
                .Where(l => l.ScopeId == setId && l.MaterialKey == materialKey).ToListAsync();
            // Ровно одна связка: копия поправила ту, что была, а не завела вторую на тот же материал.
            var link = Assert.Single(links);
            Assert.Equal(qualityDocId, link.QualityDocumentId);
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
}
