using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Раскладка таблиц КП (change <c>plan-13-offers</c>) — вручную, как у сметы (<see cref="EstimateModel" />).
///
/// <para><b>Ссылки на записи ядра</b> (стройка, заказчик, вид работы) — идентификаторами без внешних ключей
/// (ТЗ CORE-34.1), с индексом, где колонка первая: вопрос «кто держит» задаётся при каждом удалении записи.
/// Ссылка на редакцию — своя, внешним ключом без каскада: редакцию, из которой сделано КП, база не даст
/// удалить и запросу, проверивший фиксацию раньше.</para>
///
/// <para><b>Деньги</b> — <c>numeric(18,2)</c>, наценки и ставки — <c>numeric(7,2)</c> (проценты до сотой),
/// объёмы — <c>numeric(18,6)</c>, как у строк сметы.</para>
/// </summary>
internal static class OfferModel
{
    public static void Configure(ModelBuilder builder)
    {
        var offer = builder.Entity<Offer>();
        offer.ToTable("offers");
        offer.HasKey(o => o.Id);
        offer.Property(o => o.Id).HasColumnName("id");
        offer.Property(o => o.Number).HasColumnName("number").HasMaxLength(Offer.NumberLength);
        offer.Property(o => o.Year).HasColumnName("year");
        offer.Property(o => o.Sequence).HasColumnName("sequence");
        offer.Property(o => o.Date).HasColumnName("date");
        offer.Property(o => o.DocumentTypeId).HasColumnName("document_type_id");
        offer.Property(o => o.ConstructionId).HasColumnName("construction_id");
        offer.Property(o => o.CustomerId).HasColumnName("customer_id");
        offer.Property(o => o.RevisionId).HasColumnName("revision_id");
        offer.Property(o => o.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        offer.Property(o => o.CurrentVersion).HasColumnName("current_version");
        offer.Property(o => o.VatMode).HasColumnName("vat_mode").HasConversion<string>().HasMaxLength(16);
        offer.Property(o => o.VatRate).HasColumnName("vat_rate").HasPrecision(7, 2);
        offer.Property(o => o.Markup).HasColumnName("markup").HasPrecision(7, 2);
        offer.Property(o => o.LinesTotal).HasColumnName("lines_total").HasPrecision(18, 2);
        offer.Property(o => o.VatAmount).HasColumnName("vat_amount").HasPrecision(18, 2);
        offer.Property(o => o.Total).HasColumnName("total").HasPrecision(18, 2);
        offer.Property(o => o.Data).HasColumnName("data").HasColumnType("jsonb");
        offer.Property(o => o.CreatedAt).HasColumnName("created_at");
        offer.Property(o => o.CreatedBy).HasColumnName("created_by");
        offer.Property(o => o.CreatedByName).HasColumnName("created_by_name").HasMaxLength(Offer.UserNameLength);
        offer.Property(o => o.ChangedAt).HasColumnName("changed_at");
        offer.Ignore(o => o.Label);
        offer.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();

        // Номер сквозной по году: два КП, заведённые разом, получили бы один номер — второй проигрывает здесь.
        offer.HasIndex(o => new { o.Year, o.Sequence }).IsUnique().HasDatabaseName(PlanDbContext.OfferNumberKey);
        offer.HasIndex(o => o.Number).IsUnique().HasDatabaseName("ux_offers_number");
        offer.HasIndex(o => o.ConstructionId).HasDatabaseName("ix_offers_construction_id");
        offer.HasIndex(o => o.CustomerId).HasDatabaseName("ix_offers_customer_id").HasFilter("customer_id IS NOT NULL");
        offer.HasIndex(o => o.DocumentTypeId).HasDatabaseName("ix_offers_document_type_id");
        offer.HasOne<EstimateRevision>().WithMany().HasForeignKey(o => o.RevisionId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName(PlanDbContext.OfferRevisionKey);
        offer.HasIndex(o => o.RevisionId).HasDatabaseName("ix_offers_revision_id");

        offer.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OfferId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_offer_lines_offer");
        offer.HasMany(o => o.SectionMarkups).WithOne().HasForeignKey(m => m.OfferId).OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_offer_section_markups_offer");

        var line = builder.Entity<OfferLine>();
        line.ToTable("offer_lines");
        line.HasKey(l => l.Id);
        line.Property(l => l.Id).HasColumnName("id");
        line.Property(l => l.OfferId).HasColumnName("offer_id");
        line.Property(l => l.Order).HasColumnName("order");
        line.Property(l => l.RevisionRowId).HasColumnName("revision_row_id");
        line.Property(l => l.WorkTypeId).HasColumnName("work_type_id");
        line.Property(l => l.Material).HasColumnName("material");
        line.Property(l => l.LsrSectionCode).HasColumnName("lsr_section_code").HasMaxLength(EstimateRow.SectionCodeLength);
        line.Property(l => l.LsrSectionName).HasColumnName("lsr_section_name").HasMaxLength(EstimateRow.SectionNameLength);
        line.Property(l => l.Number).HasColumnName("number").HasMaxLength(EstimateRow.NumberLength);
        line.Property(l => l.Name).HasColumnName("name");
        line.Property(l => l.Volume).HasColumnName("volume").HasPrecision(18, 6);
        line.Property(l => l.Unit).HasColumnName("unit").HasMaxLength(EstimateRow.UnitLength);
        line.Property(l => l.Markup).HasColumnName("markup").HasPrecision(7, 2);
        line.Property(l => l.Price).HasColumnName("price").HasPrecision(18, 2);
        line.Property(l => l.Amount).HasColumnName("amount").HasPrecision(18, 2);
        line.Ignore(l => l.Unmatched);
        line.HasIndex(l => new { l.OfferId, l.Order }).HasDatabaseName("ix_offer_lines_offer_order");
        line.HasIndex(l => l.WorkTypeId).HasDatabaseName("ix_offer_lines_work_type_id").HasFilter("work_type_id IS NOT NULL");
        // Строка редакции держится базой так же, как редакция: редакция КП зафиксирована и не удаляется.
        line.HasOne<EstimateRow>().WithMany().HasForeignKey(l => l.RevisionRowId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_offer_lines_revision_row");
        line.HasIndex(l => l.RevisionRowId).HasDatabaseName("ix_offer_lines_revision_row_id");

        var markup = builder.Entity<OfferSectionMarkup>();
        markup.ToTable("offer_section_markups");
        markup.HasKey(m => new { m.OfferId, m.SectionCode });
        markup.Property(m => m.OfferId).HasColumnName("offer_id");
        markup.Property(m => m.SectionCode).HasColumnName("section_code").HasMaxLength(EstimateRow.SectionCodeLength);
        markup.Property(m => m.Markup).HasColumnName("markup").HasPrecision(7, 2);
    }
}
