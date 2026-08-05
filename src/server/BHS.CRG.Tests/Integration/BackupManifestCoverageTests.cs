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
        // ── Проектные данные: копия переносит конфигурацию системы, а не работу по объекту ──
        ["Construction"] = "проектные данные: стройка",
        ["Section"] = "проектные данные: раздел",
        ["DocumentSet"] = "проектные данные: комплект",
        // Сам DomainObject покрыт (общие данные); документы отличаются наличием фасеты, и в копию
        // не идут — экспорт отбирает Facet == null.
        ["DocumentFacet"] = "проектные данные: документная фасета (она и отличает документ)",
        ["GeneratedFile"] = "проектные данные: выпущенные файлы",
        ["DocumentSetOutput"] = "проектные данные: сборка комплекта",

        // ── Наборы данных: сырьё и его разбор, привязанные к проекту и к крупным блобам ──
        ["DataSetFile"] = "сырьё набора данных: файл в хранилище, к конфигурации не относится",
        ["DataSetSource"] = "разбор конкретного файла: без файла бессмыслен",
        ["DataSetBinding"] = "привязка источника к конкретному экземпляру документа",
        ["DataSetProcessingTemplate"] = "ОТКРЫТО: кандидат в копию, решение в issue #687",

        // ── Документы качества ──
        ["QualityDocument"] = "ОТКРЫТО: библиотека качества, решение в issue #687",
        ["MaterialQualityLink"] = "связка документа качества с материалом проекта",
        ["QualityAuditRun"] = "результат прогона проверки, пересчитывается",

        // ── Сверка ──
        ["ReconciliationDefinition"] = "ОТКРЫТО: определения сверок, решение в issue #687",
        ["ReconciliationRun"] = "результат прогона, пересчитывается",
        ["ReconciliationFinding"] = "результат прогона, пересчитывается",
        ["ReconciliationDecision"] = "решение по находке конкретного прогона",
        ["AgentObservation"] = "наблюдения агента по конкретному прогону",

        // ── Производное состояние ──
        // Реестр блобов (issue #672) восстанавливается сам, причём двумя путями сразу: PutAsync
        // записывает каждый возвращаемый объект, а сбор на старте проходит по данным. Класть его в
        // копию значило бы переносить пути к объектам, которых в целевом хранилище может не быть, —
        // и получить реестр, обещающий больше, чем есть.
        ["BlobRegistryEntry"] = "производное: восстанавливается из данных и из самих возвращаемых блобов",

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
                NullLogger<BackupService>.Instance).ExportAsync();

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

        var alias = ReconciliationAlias.Propose(
            $"key-{Guid.NewGuid():N}", "Вариант", $"canon-{Guid.NewGuid():N}", "Канон", null, "человек");
        alias.Review(AliasStatus.Confirmed, null, "человек");
        db.ReconciliationAliases.Add(alias);

        await db.SaveChangesAsync();
    }
}
