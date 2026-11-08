using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Раскладка таблиц распределения (change <c>plan-10-distribution</c>), вручную, как у сметы
/// (<see cref="EstimateModel" />): заголовок распределения редакции, правила разделов ЛСР, части строк и отчёт
/// переноса.
///
/// <para>Всё распределение уходит вместе с редакцией (каскад): отдельно от строк оно ничего не значит. Раздел
/// стройки — запись ядра, идентификатором без внешнего ключа (ТЗ CORE-34.1), с индексом, где он первый: вопрос
/// «кто держит» задаётся при каждом удалении раздела.</para>
/// </summary>
internal static class DistributionModel
{
    /// <summary>Ключ заголовка: два одновременных первых распределения редакции — одно записано, второе отказ.</summary>
    internal const string HeadKey = "pk_revision_distributions";

    /// <summary>Одно правило на раздел ЛСР: его нарушает только одновременное заведение того же правила.</summary>
    internal const string RuleKey = "ux_distribution_rules_revision_section";

    /// <summary>Один раздел стройки на строку: его нарушает только одновременная запись той же части.</summary>
    internal const string PartKey = "ux_distribution_parts_row_section";

    /// <summary>
    /// Отказы базы, которые значат «распределение тем временем изменили». Проигравшая одновременная запись
    /// упирается в любой из них — в xmin заголовка, но и в единственность части или правила: оба оператора идут одним
    /// пакетом, и какой откажет первым, не определено.
    /// </summary>
    internal static readonly IReadOnlySet<string> RaceKeys = new HashSet<string> { HeadKey, RuleKey, PartKey };

    public static void Configure(ModelBuilder builder)
    {
        var head = builder.Entity<RevisionDistribution>();
        head.ToTable("revision_distributions");
        head.HasKey(h => h.RevisionId).HasName(HeadKey);
        head.Property(h => h.RevisionId).HasColumnName("revision_id");
        head.Property(h => h.EstimateId).HasColumnName("estimate_id");
        head.Property(h => h.Label).HasColumnName("label").HasMaxLength(RevisionDistribution.LabelLength);
        head.Property(h => h.ChangedAt).HasColumnName("changed_at");
        head.Property(h => h.ChangedBy).HasColumnName("changed_by");
        head.Property(h => h.ChangedByName).HasColumnName("changed_by_name");
        head.Property(h => h.CarriedFromRevisionId).HasColumnName("carried_from_revision_id");
        head.Property(h => h.CarriedFromNumber).HasColumnName("carried_from_number");
        head.Property(h => h.CarriedRows).HasColumnName("carried_rows");
        head.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();
        head.HasOne<EstimateRevision>().WithMany().HasForeignKey(h => h.RevisionId).OnDelete(DeleteBehavior.Cascade);
        // Смета с редакциями не удаляется вовсе (EstimateModel) — ключ к ней держит только связность.
        head.HasOne<Estimate>().WithMany().HasForeignKey(h => h.EstimateId).OnDelete(DeleteBehavior.Restrict);
        head.HasIndex(h => h.EstimateId).HasDatabaseName("ix_revision_distributions_estimate_id");
        // Источник переноса удалили — номер остаётся словами (CarriedFromNumber), ссылка обнуляется.
        head.HasOne<EstimateRevision>().WithMany().HasForeignKey(h => h.CarriedFromRevisionId)
            .OnDelete(DeleteBehavior.SetNull);
        head.HasIndex(h => h.CarriedFromRevisionId).HasDatabaseName("ix_revision_distributions_carried_from")
            .HasFilter("carried_from_revision_id IS NOT NULL");

        var rule = builder.Entity<DistributionRule>();
        rule.ToTable("distribution_rules");
        rule.HasKey(r => r.Id);
        rule.Property(r => r.Id).HasColumnName("id");
        rule.Property(r => r.RevisionId).HasColumnName("revision_id");
        rule.Property(r => r.LsrSectionCode).HasColumnName("lsr_section_code").HasMaxLength(EstimateRow.SectionCodeLength);
        rule.Property(r => r.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        rule.Property(r => r.SectionId).HasColumnName("section_id");
        rule.Ignore(r => r.Target);
        rule.HasOne<EstimateRevision>().WithMany().HasForeignKey(r => r.RevisionId).OnDelete(DeleteBehavior.Cascade);
        // Одно правило на раздел ЛСР, и строки вне разделов (код null) — тоже одним: NULL здесь не «разные».
        rule.HasIndex(r => new { r.RevisionId, r.LsrSectionCode }).IsUnique().AreNullsDistinct(false)
            .HasDatabaseName(RuleKey);
        rule.HasIndex(r => r.SectionId).HasDatabaseName("ix_distribution_rules_section_id")
            .HasFilter("section_id IS NOT NULL");

        var part = builder.Entity<DistributionPart>();
        part.ToTable("distribution_parts");
        part.HasKey(p => p.Id);
        part.Property(p => p.Id).HasColumnName("id");
        part.Property(p => p.RevisionId).HasColumnName("revision_id");
        part.Property(p => p.RowId).HasColumnName("row_id");
        part.Property(p => p.SectionId).HasColumnName("section_id");
        part.Property(p => p.Quantity).HasColumnName("quantity").HasPrecision(18, 6);
        part.HasOne<EstimateRevision>().WithMany().HasForeignKey(p => p.RevisionId).OnDelete(DeleteBehavior.Cascade);
        part.HasOne<EstimateRow>().WithMany().HasForeignKey(p => p.RowId).OnDelete(DeleteBehavior.Cascade);
        // Раздел — один раз на строку: две части на одну цель были бы одной частью, записанной дважды.
        part.HasIndex(p => new { p.RowId, p.SectionId }).IsUnique().HasDatabaseName(PartKey);
        part.HasIndex(p => p.RevisionId).HasDatabaseName("ix_distribution_parts_revision_id");
        part.HasIndex(p => p.SectionId).HasDatabaseName("ix_distribution_parts_section_id");

        var item = builder.Entity<CarryOverItem>();
        item.ToTable("distribution_carry_over");
        item.HasKey(i => i.Id);
        item.Property(i => i.Id).HasColumnName("id");
        item.Property(i => i.RevisionId).HasColumnName("revision_id");
        item.Property(i => i.Category).HasColumnName("category").HasConversion<string>().HasMaxLength(16);
        item.Property(i => i.Subject).HasColumnName("subject").HasConversion<string>().HasMaxLength(16);
        item.Property(i => i.SourceRowId).HasColumnName("source_row_id");
        item.Property(i => i.LsrSectionCode).HasColumnName("lsr_section_code").HasMaxLength(EstimateRow.SectionCodeLength);
        item.Property(i => i.LsrSectionName).HasColumnName("lsr_section_name").HasMaxLength(CarryOverItem.SectionNameLength);
        item.Property(i => i.RateCode).HasColumnName("rate_code").HasMaxLength(EstimateRow.RateCodeLength);
        item.Property(i => i.NumberInSection).HasColumnName("number_in_section").HasMaxLength(EstimateRow.NumberLength);
        item.Property(i => i.Number).HasColumnName("number").HasMaxLength(EstimateRow.NumberLength);
        item.Property(i => i.Name).HasColumnName("name");
        item.Property(i => i.SourceVolume).HasColumnName("source_volume").HasPrecision(18, 6);
        item.Property(i => i.SourceUnit).HasColumnName("source_unit").HasMaxLength(EstimateRow.UnitLength);
        item.Property(i => i.Parts).HasColumnName("parts").HasColumnType("jsonb");
        item.Property(i => i.RuleKind).HasColumnName("rule_kind").HasConversion<string>().HasMaxLength(16);
        item.Property(i => i.RuleSectionId).HasColumnName("rule_section_id");
        item.Property(i => i.RowId).HasColumnName("row_id");
        item.Property(i => i.NewVolume).HasColumnName("new_volume").HasPrecision(18, 6);
        item.Property(i => i.Candidates).HasColumnName("candidates");
        item.Property(i => i.Resolution).HasColumnName("resolution").HasConversion<string>().HasMaxLength(16);
        item.Property(i => i.ResolvedAt).HasColumnName("resolved_at");
        item.Ignore(i => i.Pending);
        item.Ignore(i => i.PartList);
        item.HasOne<EstimateRevision>().WithMany().HasForeignKey(i => i.RevisionId).OnDelete(DeleteBehavior.Cascade);
        item.HasOne<EstimateRow>().WithMany().HasForeignKey(i => i.RowId).OnDelete(DeleteBehavior.Cascade);
        item.HasIndex(i => i.RevisionId).HasDatabaseName("ix_distribution_carry_over_revision_id");
        item.HasIndex(i => i.RowId).HasDatabaseName("ix_distribution_carry_over_row_id").HasFilter("row_id IS NOT NULL");
    }
}
