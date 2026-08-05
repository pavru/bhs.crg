using BHS.CRG.Application.Backup;
using BHS.CRG.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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
        ["DomainObject.Document"] = "проектные данные: документы (общие данные из DomainObject входят)",
        ["DocumentFacet"] = "проектные данные: документная фасета",
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
}
