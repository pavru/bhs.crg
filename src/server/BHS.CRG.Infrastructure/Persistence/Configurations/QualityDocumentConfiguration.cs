using BHS.CRG.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class QualityDocumentConfiguration : IEntityTypeConfiguration<QualityDocument>
{
    public void Configure(EntityTypeBuilder<QualityDocument> b)
    {
        b.ToTable("quality_documents");
        b.HasKey(e => e.Id);
        b.Property(e => e.DocumentTypeId).IsRequired();
        b.Property(e => e.DisplayName).HasMaxLength(512).IsRequired();
        b.Property(e => e.Requisites).HasColumnType("jsonb").IsRequired();
        b.Property(e => e.ScanBlobPath).HasMaxLength(1024);
        b.Property(e => e.ScanFileName).HasMaxLength(512);
        b.Property(e => e.ScanMimeType).HasMaxLength(256);
        b.Property(e => e.Source).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(e => e.SourceUrl).HasMaxLength(2048);
        b.Property(e => e.Scope).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.HasIndex(e => new { e.Scope, e.ScopeId });
        b.HasIndex(e => e.DocumentTypeId);
    }
}

public class MaterialQualityLinkConfiguration : IEntityTypeConfiguration<MaterialQualityLink>
{
    public void Configure(EntityTypeBuilder<MaterialQualityLink> b)
    {
        b.ToTable("material_quality_links");
        b.HasKey(e => e.Id);
        // 1024, а не 512: ключ стал СОСТАВНЫМ (#582) — склейка всех полей идентичности всех типов
        // материала, а тэг допустим и на text-поле. Четыре описательных компонента упирались в 512,
        // и упор этот — не молчаливый: SaveChanges бросает и отменяет весь пакет привязок.
        // Выше не берём из-за btree: 1024 символа кириллицы ≈ 2 КБ, вместе с Scope и ScopeId это
        // ещё внутри лимита ключа индекса.
        b.Property(e => e.MaterialKey).HasMaxLength(1024).IsRequired();
        b.Property(e => e.MaterialLabel).HasMaxLength(512);
        b.Property(e => e.Scope).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(e => e.QualityDocumentId).IsRequired();

        // Уникальность — инвариант «на материал в области ровно одна связь» (issue #554). До сих пор он
        // держался только кодом команды (Retarget вместо второй записи), а SetMaterialLinksCommand
        // строит словарь по ключу и на дубле БРОСАЕТ. Дубль рождался гонкой двух параллельных POST.
        // AreNullsDistinct(false) — без него ограничение НЕ ДЕЙСТВУЕТ там, где лежат все данные:
        // PostgreSQL считает NULL-ы различными, а у System-связок ScopeId равен NULL (все 113 живых
        // связок именно такие). Уникальность без этого была бы видимостью инварианта.
        b.HasIndex(e => new { e.Scope, e.ScopeId, e.MaterialKey }).IsUnique().AreNullsDistinct(false);
        b.HasIndex(e => e.QualityDocumentId);

        // Внешний ключ без навигации: связка ссылается на документ голым Guid, и чистка при удалении
        // документа жила руками в обработчике. Восстановление бэкапа или прямой SQL оставляли висячие
        // связки — экран контроля показал бы строку с пустым именем.
        b.HasOne<QualityDocument>().WithMany()
            .HasForeignKey(e => e.QualityDocumentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
