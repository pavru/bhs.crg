using BHS.CRG.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class BlobRegistryConfiguration : IEntityTypeConfiguration<BlobRegistryEntry>
{
    public void Configure(EntityTypeBuilder<BlobRegistryEntry> b)
    {
        b.ToTable("blob_registry");
        b.HasKey(e => e.Id);

        // 1024 — как у DataSetFile.BlobPath и TemplateAsset.BlobPath: путь один и тот же,
        // и расходиться пределам нельзя, иначе запись пройдёт в одном месте и упрётся в другом.
        b.Property(e => e.Path).HasMaxLength(1024).IsRequired();
        b.Property(e => e.FileName).HasMaxLength(512);
        b.Property(e => e.MimeType).HasMaxLength(128);

        // Уникальность — не украшение: наполнение идёт из двух источников (запись в хранилище и
        // разовый сбор по существующим данным), и обе стороны обязаны быть идемпотентны. Индекс
        // при этом и есть тот самый один запрос, ради которого заведена таблица.
        b.HasIndex(e => e.Path).IsUnique();
    }
}
