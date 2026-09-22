using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

// Identity (AspNetUsers/Roles) и доменные агрегаты намеренно в одном DbContext / одной истории
// миграций. Разделять только при появлении конкретной причины: раздельное масштабирование,
// независимое версионирование Identity или явная мультитенантность — сейчас (один Postgres, один
// API-контейнер) разделение добавило бы сложность (два набора миграций, транзакции через границу)
// без требования. См. память project_data_access_convention.
public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<CatalogEntity> CatalogEntities => Set<CatalogEntity>();
    public DbSet<BHS.CRG.Domain.Objects.DomainObject> DomainObjects => Set<BHS.CRG.Domain.Objects.DomainObject>();
    public DbSet<BHS.CRG.Domain.Objects.DocumentFacet> DocumentFacets => Set<BHS.CRG.Domain.Objects.DocumentFacet>();
    public DbSet<PrimitiveType> PrimitiveTypes => Set<PrimitiveType>();
    public DbSet<EnumType> EnumTypes => Set<EnumType>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateAsset> TemplateAssets => Set<TemplateAsset>();
    public DbSet<BHS.CRG.Domain.Recognition.RecognitionProfile> RecognitionProfiles => Set<BHS.CRG.Domain.Recognition.RecognitionProfile>();
    public DbSet<Construction> Constructions => Set<Construction>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<DocumentSet> DocumentSets => Set<DocumentSet>();
    public DbSet<DocumentSetPlanItem> DocumentSetPlans => Set<DocumentSetPlanItem>();
    public DbSet<GeneratedFile> GeneratedFiles => Set<GeneratedFile>();
    public DbSet<DocumentSetOutput> DocumentSetOutputs => Set<DocumentSetOutput>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<TypstUserLib> TypstUserLibs => Set<TypstUserLib>();
    public DbSet<TypstUserLibFile> TypstUserLibFiles => Set<TypstUserLibFile>();
    public DbSet<DataSetFile> DataSetFiles => Set<DataSetFile>();
    public DbSet<DataSetSource> DataSetSources => Set<DataSetSource>();
    public DbSet<DataSetBinding> DataSetBindings => Set<DataSetBinding>();
    public DbSet<DataSetBindingTemplate> DataSetBindingTemplates => Set<DataSetBindingTemplate>();
    public DbSet<DataSetProcessingTemplate> DataSetProcessingTemplates => Set<DataSetProcessingTemplate>();
    public DbSet<QualityDocument> QualityDocuments => Set<QualityDocument>();
    public DbSet<MaterialQualityLink> MaterialQualityLinks => Set<MaterialQualityLink>();
    public DbSet<QualityAuditRun> QualityAuditRuns => Set<QualityAuditRun>();
    public DbSet<BHS.CRG.Domain.Settings.IntegrationSettingsEntity> IntegrationSettings => Set<BHS.CRG.Domain.Settings.IntegrationSettingsEntity>();
    public DbSet<BHS.CRG.Domain.Settings.ServiceStateEntity> ServiceState => Set<BHS.CRG.Domain.Settings.ServiceStateEntity>();

    /// <summary>Предпочтения пользователя на сервере (ТЗ CORE-25.3): тема, язык и что придёт дальше.</summary>
    public DbSet<BHS.CRG.Domain.Settings.UserSetting> UserSettings => Set<BHS.CRG.Domain.Settings.UserSetting>();
    public DbSet<BHS.CRG.Domain.Notifications.Notification> Notifications => Set<BHS.CRG.Domain.Notifications.Notification>();
    public DbSet<BHS.CRG.Domain.Notifications.NotificationUserState> NotificationUserStates
        => Set<BHS.CRG.Domain.Notifications.NotificationUserState>();
    public DbSet<BHS.CRG.Domain.Jobs.Job> Jobs => Set<BHS.CRG.Domain.Jobs.Job>();
    public DbSet<BHS.CRG.Domain.Reconciliation.ReconciliationDefinition> Reconciliations
        => Set<BHS.CRG.Domain.Reconciliation.ReconciliationDefinition>();
    public DbSet<BHS.CRG.Domain.Reconciliation.ReconciliationRun> ReconciliationRuns
        => Set<BHS.CRG.Domain.Reconciliation.ReconciliationRun>();
    public DbSet<BHS.CRG.Domain.Reconciliation.ReconciliationFinding> ReconciliationFindings
        => Set<BHS.CRG.Domain.Reconciliation.ReconciliationFinding>();
    public DbSet<BHS.CRG.Domain.Reconciliation.ReconciliationDecision> ReconciliationDecisions
        => Set<BHS.CRG.Domain.Reconciliation.ReconciliationDecision>();
    public DbSet<BHS.CRG.Domain.Reconciliation.AgentObservation> AgentObservations
        => Set<BHS.CRG.Domain.Reconciliation.AgentObservation>();
    public DbSet<BHS.CRG.Domain.Reconciliation.ReconciliationAlias> ReconciliationAliases
        => Set<BHS.CRG.Domain.Reconciliation.ReconciliationAlias>();
    public DbSet<BHS.CRG.Domain.Support.BugReport> BugReports
        => Set<BHS.CRG.Domain.Support.BugReport>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>Права, объявленные кодом (AUTH-1): наполняется при старте, правится только кодом.</summary>
    public DbSet<BHS.CRG.Domain.Auth.Permission> Permissions
        => Set<BHS.CRG.Domain.Auth.Permission>();
    public DbSet<BHS.CRG.Domain.Storage.BlobRegistryEntry> BlobRegistry
        => Set<BHS.CRG.Domain.Storage.BlobRegistryEntry>();

    /// <summary>
    /// Журнал действий (ТЗ CORE-28). Обращаться к набору напрямую позволено ОДНОЙ службе —
    /// <c>Infrastructure/Activity/ActivityLog.cs</c>; сторож <c>ActivityLogInventoryTests</c>
    /// перечисляет упоминания и падает на новом.
    /// </summary>
    public DbSet<BHS.CRG.Domain.Activity.ActivityRecord> ActivityRecords
        => Set<BHS.CRG.Domain.Activity.ActivityRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RefuseActivityLogEdits();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        RefuseActivityLogEdits();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    /// <summary>
    /// Журнал только дописывается (ТЗ CORE-28): правка и удаление записи отвергаются здесь, в
    /// единственной точке сохранения.
    ///
    /// Приватных сеттеров для этого мало: <c>ChangeTracker</c> ставит состояние <c>Modified</c> и по
    /// прямому <c>Entry(...).State</c>, и по правке через рефлексию, и запись ушла бы в базу без
    /// единого признака. Отказ громкий и без обработки — поправить журнал может только код, а код
    /// чинят, а не уговаривают.
    ///
    /// ⚠️ Чего это НЕ закрывает: <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> и голый SQL идут мимо
    /// трекера. Их закрывает сторож по исходникам — там, где такую строку ещё можно не дописать.
    /// </summary>
    private void RefuseActivityLogEdits()
    {
        var touched = ChangeTracker.Entries<BHS.CRG.Domain.Activity.ActivityRecord>()
            .Where(e => e.State is EntityState.Modified or EntityState.Deleted)
            .Select(e => $"{e.Entity.Action} от {e.Entity.OccurredAt:u} ({e.State})")
            .ToList();

        if (touched.Count == 0) return;

        throw new InvalidOperationException(
            "Записи журнала действий изменению и удалению не подлежат (ТЗ CORE-28), а изменены: " +
            string.Join("; ", touched) +
            ". Новое событие записывается новой строкой через IActivityLog.RecordAsync.");
    }
}
