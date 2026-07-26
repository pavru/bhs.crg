using BHS.CRG.Domain.Reconciliation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Infrastructure.Persistence.Configurations;

public class ReconciliationDefinitionConfiguration : IEntityTypeConfiguration<ReconciliationDefinition>
{
    public void Configure(EntityTypeBuilder<ReconciliationDefinition> b)
    {
        b.ToTable("reconciliations");
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(512).IsRequired();
        b.Property(e => e.Scope).HasConversion<int>();
        b.Property(e => e.Spec).HasColumnType("jsonb").IsRequired();
        b.HasIndex(e => new { e.Scope, e.ScopeId });
    }
}

public class ReconciliationRunConfiguration : IEntityTypeConfiguration<ReconciliationRun>
{
    public void Configure(EntityTypeBuilder<ReconciliationRun> b)
    {
        b.ToTable("reconciliation_runs");
        b.HasKey(e => e.Id);
        b.Property(e => e.Status).HasConversion<int>();
        b.Property(e => e.Error).HasMaxLength(2048);
        b.HasIndex(e => new { e.DefinitionId, e.StartedAt });

        // Прогон — снимок определения; удалили определение, история смысла не имеет.
        b.HasOne<ReconciliationDefinition>().WithMany()
            .HasForeignKey(e => e.DefinitionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ReconciliationFindingConfiguration : IEntityTypeConfiguration<ReconciliationFinding>
{
    public void Configure(EntityTypeBuilder<ReconciliationFinding> b)
    {
        b.ToTable("reconciliation_findings");
        b.HasKey(e => e.Id);
        b.Property(e => e.Key).HasMaxLength(1024).IsRequired();
        b.Property(e => e.Label).HasMaxLength(1024).IsRequired();
        b.Property(e => e.Status).HasConversion<int>();
        b.Property(e => e.Provenance).HasColumnType("jsonb").IsRequired();

        // Поиск «что было с этим ключом раньше» — основной запрос: им вычисляется «Устранено».
        b.HasIndex(e => new { e.RunId, e.Key });

        b.HasOne<ReconciliationRun>().WithMany()
            .HasForeignKey(e => e.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ReconciliationDecisionConfiguration : IEntityTypeConfiguration<ReconciliationDecision>
{
    public void Configure(EntityTypeBuilder<ReconciliationDecision> b)
    {
        b.ToTable("reconciliation_decisions");
        b.HasKey(e => e.Id);
        b.Property(e => e.Key).HasMaxLength(1024).IsRequired();
        b.Property(e => e.Kind).HasConversion<int>();
        b.Property(e => e.Note).HasMaxLength(2048);
        b.Property(e => e.DecidedBy).HasMaxLength(256);

        // Одно решение на позицию: второе по тому же ключу — это правка первого, а не новая запись,
        // иначе «какое из двух действует» становится неопределённым.
        b.HasIndex(e => new { e.DefinitionId, e.Key }).IsUnique();

        // Решение НЕ привязано к прогону и переживает его — это и есть память журнала (#414).
        b.HasOne<ReconciliationDefinition>().WithMany()
            .HasForeignKey(e => e.DefinitionId).OnDelete(DeleteBehavior.Cascade);
    }
}
