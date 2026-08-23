using System.IO.Compression;
using System.Text.Json;
using BHS.CRG.Application.Backup;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Recognition;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Domain.Reconciliation;
using BHS.CRG.Domain.Recognition;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BHS.CRG.Tests.Integration;

/// <summary>
/// Каждая сущность модели должна быть ЛИБО представлена в манифесте резервной копии, ЛИБО названа
/// в списке исключений с причиной.
///
/// Тест написан не ради нынешнего состава, а против дрейфа: до него ничто не связывало «добавили
/// таблицу» с «примите решение про бэкап», и пропуск обнаруживался только при восстановлении —
/// то есть тогда, когда данные уже нужны. Обе находки ревизии 2026-08-05
/// (<c>DataSetBindingTemplates</c>, <c>ReconciliationAliases</c>) были бы пойманы в день появления.
///
/// Список исключений ниже — заодно и запись политики: до сих пор она жила в комментариях и в
/// голове. Граница (issue #403): копия конфигурационно-справочная, а не полный дамп.
/// </summary>
[Collection("Integration")]
public class BackupManifestCoverageTests(IntegrationTestFixture fixture)
{
    /// <summary>
    /// Сущности, сознательно НЕ входящие в резервную копию, и почему. Добавляя сюда строку,
    /// вы принимаете решение — именно этого тест и добивается.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyExcluded = new()
    {
        // ── Проектные данные ──
        // С issue #833 стройки, разделы, комплекты, документы с фасетой и выпущенными файлами,
        // наборы данных, сверки и связки с материалами В КОПИЮ ВХОДЯТ — но только в полную. Здесь
        // остаётся то, что не входит ни в какую.
        ["DocumentSetOutput"] = "сборка комплекта — производное от документов, пересобирается кнопкой",

        // След работы фоновой службы, а не пользовательские данные (issue #813): «о какой версии
        // уже уведомили» и «когда удачно проверяли». Восстановленный на другой машине, он ввёл бы
        // в заблуждение — там своя история проверок; а на этой воссоздаётся сам, первой же
        // проверкой. Настройка (включена ли проверка) в копию идёт — она в IntegrationSettings.
        ["ServiceStateEntity"] = "состояние службы: воссоздаётся само, переносить между установками нечего",

        // ── Документы качества ──
        ["QualityAuditRun"] = "результат прогона проверки, пересчитывается",

        // ── Сверка ──
        // Решение #687 («спека адресует источники по идентификатору, а их на целевой системе нет
        // и не будет») отменено issue #833 — ровно тем, что источники теперь едут в той же копии и
        // с теми же идентификаторами. Определения входят в полную копию.
        ["ReconciliationRun"] = "результат прогона, пересчитывается",
        ["ReconciliationFinding"] = "результат прогона, пересчитывается",
        // Решение человека по расхождению. Определение сверки теперь в копии есть (#833), но
        // решение относится к КОНКРЕТНОМУ расхождению конкретного прогона, а прогоны не
        // переносятся — приложить его на целевой системе не к чему.
        ["ReconciliationDecision"] = "относится к расхождению конкретного прогона, а прогоны не переносятся",
        ["AgentObservation"] = "наблюдения агента по конкретному прогону",

        // ── Производное состояние ──
        // Реестр блобов (issue #672) восстанавливается сам, причём двумя путями сразу: PutAsync
        // записывает каждый возвращаемый объект, а сбор на старте проходит по данным. Класть его в
        // копию значило бы переносить пути к объектам, которых в целевом хранилище может не быть, —
        // и получить реестр, обещающий больше, чем есть.
        ["BlobRegistryEntry"] = "производное: восстанавливается из данных и из самих возвращаемых блобов",

        // ── Сообщения об ошибках (issue #834) ──
        // Переписка о дефектах ЭТОЙ установки: у каждого сообщения автор — её пользователь, а
        // учётные записи копия не переносит («переносятся отдельно», строкой ниже). Перевезённое
        // сообщение указывало бы на несуществующего человека, а разбирать его на новой системе
        // некому: issue по нему либо уже заведён, либо не будет заведён никогда.
        ["BugReport"] = "переписка о дефектах установки: привязана к её учётным записям, а те не переносятся",

        // ── Runtime и секреты ──
        ["Job"] = "runtime: очередь фоновых задач",
        ["Notification"] = "runtime: уведомления",
        ["Subscription"] = "runtime: подписки пользователей на уведомления",
        ["IntegrationSettingsEntity"] = "СЕКРЕТЫ: ключи интеграций и пароль SMTP в копию не кладём",
        ["RefreshToken"] = "СЕКРЕТЫ: сессии пользователей",

        // ── Identity ──
        ["ApplicationUser"] = "учётные записи: переносятся отдельно, не конфигурация",
        ["IdentityRole"] = "роли создаёт приложение при старте",
        ["IdentityUserRole"] = "Identity: связь пользователь-роль",
        ["IdentityUserClaim"] = "Identity",
        ["IdentityUserLogin"] = "Identity",
        ["IdentityUserToken"] = "Identity",
        ["IdentityRoleClaim"] = "Identity",
    };

    /// <summary>
    /// Имя типа без арности: CLR отдаёт обобщённые как <c>IdentityRole`1</c>, и держать в списках
    /// такое имя — значит ломать их при малейшей смене параметра типа.
    /// </summary>
    private static string NameOf(Type t) =>
        t.Name.IndexOf('`') is var i && i > 0 ? t.Name[..i] : t.Name;

    /// <summary>Сущность → свойство манифеста, которым она представлена.</summary>
    private static readonly Dictionary<string, string> CoveredByManifest = new()
    {
        ["DocumentType"] = nameof(BackupManifest.DocumentTypes),
        ["Template"] = nameof(BackupManifest.Templates),
        ["TemplateAsset"] = nameof(BackupManifest.TemplateAssets),
        ["CatalogEntity"] = nameof(BackupManifest.CatalogEntities),
        ["DomainObject"] = nameof(BackupManifest.CommonDataEntries),
        ["PrimitiveType"] = nameof(BackupManifest.PrimitiveTypes),
        ["EnumType"] = nameof(BackupManifest.EnumTypes),
        ["TypstUserLib"] = nameof(BackupManifest.TypstUserLib),
        ["TypstUserLibFile"] = nameof(BackupManifest.TypstUserLibFiles),
        ["RecognitionProfile"] = nameof(BackupManifest.RecognitionProfiles),
        ["DataSetBindingTemplate"] = nameof(BackupManifest.DataSetBindingTemplates),
        ["ReconciliationAlias"] = nameof(BackupManifest.ReconciliationAliases),
        ["DataSetProcessingTemplate"] = nameof(BackupManifest.DataSetProcessingTemplates),
        ["QualityDocument"] = nameof(BackupManifest.QualityDocuments),
        // Проектные данные (issue #833) — только в полной копии, но решение по ним принято, и
        // секция в манифесте есть.
        ["Construction"] = nameof(BackupManifest.Constructions),
        ["Section"] = nameof(BackupManifest.Sections),
        ["DocumentSet"] = nameof(BackupManifest.DocumentSets),
        // Документы и их фасета едут одной записью: фасета и есть то, чем документ отличается от
        // записи общих данных, и разъехаться им нельзя.
        ["DocumentFacet"] = nameof(BackupManifest.Documents),
        ["GeneratedFile"] = nameof(BackupManifest.Documents),
        ["DataSetFile"] = nameof(BackupManifest.DataSetFiles),
        ["DataSetSource"] = nameof(BackupManifest.DataSetSources),
        ["DataSetBinding"] = nameof(BackupManifest.DataSetBindings),
        ["ReconciliationDefinition"] = nameof(BackupManifest.Reconciliations),
        ["MaterialQualityLink"] = nameof(BackupManifest.MaterialQualityLinks),
    };

    [Fact]
    public void EveryEntity_IsEitherInManifest_OrExplicitlyExcluded()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entityNames = db.Model.GetEntityTypes()
            .Select(t => NameOf(t.ClrType))
            .Distinct()
            .ToList();

        var undecided = entityNames
            .Where(n => !CoveredByManifest.ContainsKey(n) && !DeliberatelyExcluded.ContainsKey(n))
            .OrderBy(n => n)
            .ToList();

        Assert.True(undecided.Count == 0,
            "В модели появились сущности, про которые не принято решение о резервной копии: " +
            string.Join(", ", undecided) + ".\n" +
            "Добавьте каждую ЛИБО в манифест (и в CoveredByManifest), ЛИБО в DeliberatelyExcluded " +
            "с причиной. Молча оставлять нельзя: пропуск обнаружится только при восстановлении.");
    }

    /// <summary>
    /// Обратная сторона: свойство манифеста, за которым не стоит сущности, — след переименования
    /// или удаления. Такой перекос ловится только здесь.
    /// </summary>
    [Fact]
    public void EveryCoveredEntity_StillExistsInModel()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entityNames = db.Model.GetEntityTypes().Select(t => NameOf(t.ClrType)).ToHashSet();

        var vanished = CoveredByManifest.Keys.Where(n => !entityNames.Contains(n)).OrderBy(n => n).ToList();

        Assert.True(vanished.Count == 0,
            "Манифест ссылается на сущности, которых в модели больше нет: " + string.Join(", ", vanished));
    }

    /// <summary>Свойства манифеста в списке покрытия должны существовать — защита от опечатки.</summary>
    [Fact]
    public void CoverageMap_NamesRealManifestProperties()
    {
        var props = typeof(BackupManifest).GetProperties().Select(p => p.Name).ToHashSet();
        var unknown = CoveredByManifest.Values.Where(v => !props.Contains(v)).Distinct().ToList();

        Assert.True(unknown.Count == 0,
            "В карте покрытия названы несуществующие свойства манифеста: " + string.Join(", ", unknown));
    }

    /// <summary>
    /// Карта покрытия проверяет ИМЕНА, а этот тест — что экспорт действительно кладёт данные в
    /// каждую заявленную секцию.
    ///
    /// Без него правка, из-за которой <c>BuildManifestAsync</c> перестанет заполнять секцию,
    /// оставляет все три предыдущих теста зелёными, а сущность молча исчезает из каждой новой копии
    /// — ровно тот способ потерять данные, ради закрытия которого весь этот файл и написан.
    /// </summary>
    [Fact]
    public async Task Export_FillsEverySectionDeclaredInCoverageMap()
    {
        await fixture.ResetDatabaseAsync();
        await SeedOneOfEachCoveredEntityAsync();

        BackupManifest manifest;
        using (var scope = fixture.Services.CreateScope())
        {
            var (zipStream, _) = await new BackupService(
                scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                scope.ServiceProvider.GetRequiredService<IBlobStorage>(),
                NullLogger<BackupService>.Instance).ExportAsync(BackupScope.Full);

            await using var _zipHandle = zipStream;
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
            await using var entry = zip.GetEntry("manifest.json")!.Open();
            manifest = (await JsonSerializer.DeserializeAsync<BackupManifest>(entry))!;
        }

        var empty = new List<string>();
        foreach (var (entityName, propertyName) in CoveredByManifest)
        {
            var value = typeof(BackupManifest).GetProperty(propertyName)!.GetValue(manifest);
            var filled = value is not null && (value is not System.Collections.IEnumerable e || e.GetEnumerator().MoveNext());
            if (!filled) empty.Add($"{entityName} → {propertyName}");
        }

        Assert.True(empty.Count == 0,
            "Экспорт не заполнил секции, за которые в карте покрытия отвечает сущность: " +
            string.Join(", ", empty) + ".\nЛибо сущность выпала из BuildManifestAsync, либо её надо " +
            "перенести в список исключений.");
    }

    /// <summary>По одной записи каждой сущности, которую копия обязана переносить.</summary>
    private async Task SeedOneOfEachCoveredEntityAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var blob = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
        var now = DateTimeOffset.UtcNow;
        var docTypeId = Guid.NewGuid();
        var compositeTypeId = Guid.NewGuid();

        // Профили распознавания фикстура НЕ чистит осознанно: встроенные создаёт сидер, и только
        // при старте хоста (см. FixtureResetCoverageTests). Снимаем ПОЛЬЗОВАТЕЛЬСКИЕ, оставшиеся от
        // прошлых прогонов; встроенные не трогаем — унести их отсюда значит забрать у тестов
        // распознавания, которые пойдут следом.
        db.RecognitionProfiles.RemoveRange(db.RecognitionProfiles.Where(p => p.Code == null));
        await db.SaveChangesAsync();

        db.PrimitiveTypes.Add(PrimitiveType.Restore(
            Guid.NewGuid(), "Строка", $"str-{Guid.NewGuid():N}", "string", null, JsonDocument.Parse("{}"), now, now));
        db.EnumTypes.Add(EnumType.Restore(
            Guid.NewGuid(), "Статус", $"st-{Guid.NewGuid():N}", null,
            JsonDocument.Parse("""[{"code":"a","label":"А"}]"""), now, now));
        db.DocumentTypes.Add(DocumentType.Restore(
            docTypeId, "Тип", $"t-{Guid.NewGuid():N}", DocumentTypeKind.Document, null,
            JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"), false, now, now, null, false));
        db.DocumentTypes.Add(DocumentType.Restore(
            compositeTypeId, "Составной", $"c-{Guid.NewGuid():N}", DocumentTypeKind.Composite, null,
            JsonDocument.Parse("""{"fields":[]}"""), JsonDocument.Parse("{}"), false, now, now, null, false));
        db.Templates.Add(Template.Restore(
            Guid.NewGuid(), docTypeId, "Шаблон", "#set page()", 1, true, true, now, now));

        await blob.PutAsync("assets/coverage.png", new MemoryStream([1, 2, 3]), "image/png", default);
        db.TemplateAssets.Add(TemplateAsset.Restore(
            Guid.NewGuid(), TemplateAssetScope.System, null, TemplateAssetKind.Image,
            "logo", "coverage.png", "image/png", "assets/coverage.png", null, now, now));

        db.CatalogEntities.Add(CatalogEntity.Restore(
            Guid.NewGuid(), "Organization", "ООО Ромашка", JsonDocument.Parse("{}"), null, now, now));
        db.DomainObjects.Add(DomainObject.Restore(
            Guid.NewGuid(), compositeTypeId, "Общая запись", JsonDocument.Parse("{}"),
            CatalogScope.System, null, now, now));

        db.TypstUserLibs.Add(TypstUserLib.Create("#let x() = []"));
        db.TypstUserLibFiles.Add(TypstUserLibFile.Restore(Guid.NewGuid(), "lib/a.typ", "#let a() = []", now, now));

        db.RecognitionProfiles.Add(RecognitionProfile.Create(
            "Профиль покрытия", RecognitionProfileKind.Table,
            fields: RecognitionProfileJson.WriteFields([]),
            rowColumns: RecognitionProfileJson.WriteFields([new RecognitionProfileField("Поз", "Позиция", "string")])));

        db.DataSetBindingTemplates.Add(DataSetBindingTemplate.Restore(
            Guid.NewGuid(), docTypeId, "Маппинг", null, "{}", 0, now, now));

        db.DataSetProcessingTemplates.Add(DataSetProcessingTemplate.Create(
            "Рецепт покрытия", sheetOrPath: "Лист1", columnExpressions: null,
            rowFilter: null, computedColumns: null, sortSpec: null));

        await blob.PutAsync("quality/coverage.pdf", new MemoryStream([4, 5, 6]), "application/pdf", default);
        var qualityDoc = QualityDocument.Create(
            docTypeId, "Сертификат покрытия", JsonDocument.Parse("{}"),
            CatalogScope.System, null, QualityDocSource.Manual);
        qualityDoc.SetScan("quality/coverage.pdf", "coverage.pdf", "application/pdf");
        db.QualityDocuments.Add(qualityDoc);

        var alias = ReconciliationAlias.Propose(
            $"key-{Guid.NewGuid():N}", "Вариант", $"canon-{Guid.NewGuid():N}", "Канон", null, "человек");
        alias.Review(AliasStatus.Confirmed, null, "человек");
        db.ReconciliationAliases.Add(alias);

        // ── Проектные данные (issue #833) ────────────────────────────────────
        var constructionId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var setId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var dataSetFileId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();

        db.Constructions.Add(Construction.Restore(constructionId, "Стройка покрытия", Guid.NewGuid(), null, now, now));
        db.Sections.Add(Section.Restore(sectionId, constructionId, "Раздел", null, now, now));
        db.DocumentSets.Add(DocumentSet.Restore(setId, sectionId, "Комплект", null, now, now));
        db.DomainObjects.Add(DomainObject.RestoreDocument(
            documentId, docTypeId, "Документ покрытия", JsonDocument.Parse("{}"), setId, now, now, null,
            DocumentStatus.Draft, 0, null, null, null, JsonDocument.Parse("{}")));

        db.DataSetFiles.Add(DataSetFile.Restore(dataSetFileId, "Набор", DataSetFormat.Xlsx,
            "datasets/coverage.xlsx", CatalogScope.Set, setId, null, null, null, null, now, now));
        db.DataSetSources.Add(DataSetSource.Restore(sourceId, dataSetFileId, "Лист1", "Лист1", null,
            "[]", 0, null, null, null, null, null, null, null, null, null, null, now, now));

        db.Reconciliations.Add(ReconciliationDefinition.Restore(
            Guid.NewGuid(), "Сверка покрытия", CatalogScope.Set, setId, JsonDocument.Parse("{}"), now, now));

        await db.SaveChangesAsync();

        // Файл документа и привязка — после сохранения владельцев: и то и другое адресует их
        // внешним ключом.
        await blob.PutAsync("generated/coverage.pdf", new MemoryStream([7, 8]), "application/pdf", default);
        db.GeneratedFiles.Add(GeneratedFile.Restore(
            Guid.NewGuid(), documentId, OutputFormat.Pdf, "generated/coverage.pdf", null, now, now));
        db.DataSetBindings.Add(DataSetBinding.Restore(
            Guid.NewGuid(), documentId, sourceId, null, "{}", now, now));
        db.MaterialQualityLinks.Add(MaterialQualityLink.Restore(
            Guid.NewGuid(), CatalogScope.Set, setId, "кабель ВВГнг", "Кабель", qualityDoc.Id, now, now));

        await db.SaveChangesAsync();
    }
}
