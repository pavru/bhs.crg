using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class TypstUserLibConfiguration : IEntityTypeConfiguration<TypstUserLib>
{
    public void Configure(EntityTypeBuilder<TypstUserLib> b)
    {
        b.ToTable("typst_user_lib");
        b.HasKey(e => e.Id);
        b.Property(e => e.Content).IsRequired();
    }
}
