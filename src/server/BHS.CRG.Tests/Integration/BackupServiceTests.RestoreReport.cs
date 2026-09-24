using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Отчёт восстановления — часть <see cref="BackupServiceTests" />: что копия ГОВОРИТ о данных,
/// которые вернулись не полностью.
///
/// <para>Имена тестов здесь — записанные решения о том, что считать честным отчётом
/// (<c>IsKeptButReported</c>, <c>IsSilent</c>, <c>DoesNotClaimLibraryCameBackUnlinked</c>).
/// Молчание — тоже поведение, и оно тоже закреплено: отчёт, сообщающий обо всём, читать не
/// будут.</para>
/// </summary>
public partial class BackupServiceTests
{
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
}
