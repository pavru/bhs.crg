using BHS.CRG.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class IntegrationSettingsConfiguration : IEntityTypeConfiguration<IntegrationSettingsEntity>
{
    public void Configure(EntityTypeBuilder<IntegrationSettingsEntity> b)
    {
        b.ToTable("integration_settings");
        b.HasKey(e => e.Id);
        b.Property(e => e.Data).HasColumnType("jsonb").IsRequired();
    }
}
