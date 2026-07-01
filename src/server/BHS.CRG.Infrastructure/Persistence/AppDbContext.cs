using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.DataSets;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<CatalogEntity> CatalogEntities => Set<CatalogEntity>();
    public DbSet<CommonDataEntry> CommonDataEntries => Set<CommonDataEntry>();
    public DbSet<PrimitiveType> PrimitiveTypes => Set<PrimitiveType>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<Construction> Constructions => Set<Construction>();
    public DbSet<Section> Sections => Set<Section>();
    public DbSet<DocumentSet> DocumentSets => Set<DocumentSet>();
    public DbSet<DocumentInstance> DocumentInstances => Set<DocumentInstance>();
    public DbSet<GeneratedFile> GeneratedFiles => Set<GeneratedFile>();
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

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
