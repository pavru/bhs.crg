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
    public DbSet<CommonDataEntry> CommonDataEntries => Set<CommonDataEntry>();
    public DbSet<PrimitiveType> PrimitiveTypes => Set<PrimitiveType>();
    public DbSet<EnumType> EnumTypes => Set<EnumType>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateAsset> TemplateAssets => Set<TemplateAsset>();
    public DbSet<Construction> Constructions => Set<Construction>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<DocumentSet> DocumentSets => Set<DocumentSet>();
    public DbSet<DocumentInstance> DocumentInstances => Set<DocumentInstance>();
    public DbSet<GeneratedFile> GeneratedFiles => Set<GeneratedFile>();
    public DbSet<DocumentSetOutput> DocumentSetOutputs => Set<DocumentSetOutput>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<TypstUserLib> TypstUserLibs => Set<TypstUserLib>();
    public DbSet<DataSetFile> DataSetFiles => Set<DataSetFile>();
    public DbSet<DataSetSource> DataSetSources => Set<DataSetSource>();
    public DbSet<DataSetBinding> DataSetBindings => Set<DataSetBinding>();
    public DbSet<DataSetBindingTemplate> DataSetBindingTemplates => Set<DataSetBindingTemplate>();
    public DbSet<DataSetProcessingTemplate> DataSetProcessingTemplates => Set<DataSetProcessingTemplate>();
    public DbSet<QualityDocument> QualityDocuments => Set<QualityDocument>();
    public DbSet<MaterialQualityLink> MaterialQualityLinks => Set<MaterialQualityLink>();
    public DbSet<BHS.CRG.Domain.Settings.IntegrationSettingsEntity> IntegrationSettings => Set<BHS.CRG.Domain.Settings.IntegrationSettingsEntity>();
    public DbSet<BHS.CRG.Domain.Notifications.Notification> Notifications => Set<BHS.CRG.Domain.Notifications.Notification>();
    public DbSet<BHS.CRG.Domain.Jobs.Job> Jobs => Set<BHS.CRG.Domain.Jobs.Job>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
