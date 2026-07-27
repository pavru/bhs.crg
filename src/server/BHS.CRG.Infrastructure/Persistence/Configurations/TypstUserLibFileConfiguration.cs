using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class TypstUserLibFileConfiguration : IEntityTypeConfiguration<TypstUserLibFile>
{
    public void Configure(EntityTypeBuilder<TypstUserLibFile> b)
    {
        b.ToTable("typst_user_lib_files");
        b.HasKey(e => e.Id);
        b.Property(e => e.Path).IsRequired().HasMaxLength(200);
        b.Property(e => e.Content).IsRequired();

        // Путь — это то, чем файл адресуется в импортах; два файла с одним путём означали бы, что
        // при материализации один молча затрёт другой.
        b.HasIndex(e => e.Path).IsUnique();
    }
}
