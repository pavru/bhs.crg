using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Раскладка таблиц приложений к смете (change <c>plan-11-conjuncture</c>), вручную, как у сметы
/// (<see cref="EstimateModel" />): конъюнктурный анализ с позициями и предложениями, ведомость объёмов с строками.
///
/// <para>Приложение уходит вместе со сметой (каскад; файлы убирает удаление сметы), позиции и предложения — вместе
/// с приложением. Один импорт — одно приложение: единственный индекс ключа импорта ловит повтор принятия.</para>
/// </summary>
internal static class AttachmentModel
{
    /// <summary>Анализ из этого импорта уже заведён — повтором принятия или в другой вкладке.</summary>
    internal const string ConjunctureImportKey = "ux_conjuncture_analyses_import_key";

    internal const string VolumeImportKey = "ux_volume_statements_import_key";

    public static void Configure(ModelBuilder builder)
    {
        var analysis = builder.Entity<ConjunctureAnalysis>();
        analysis.ToTable("conjuncture_analyses");
        Attachment(analysis, "conjuncture_analyses", ConjunctureImportKey);
        analysis.Property(a => a.CurrentPriceDate).HasColumnName("current_price_date");
        analysis.HasMany(a => a.Items).WithOne().HasForeignKey(i => i.AnalysisId).OnDelete(DeleteBehavior.Cascade);

        var item = builder.Entity<ConjunctureItem>();
        item.ToTable("conjuncture_items");
        item.HasKey(i => i.Id);
        item.Property(i => i.Id).HasColumnName("id");
        item.Property(i => i.AnalysisId).HasColumnName("analysis_id");
        item.Property(i => i.Order).HasColumnName("order");
        item.Property(i => i.ChapterIndex).HasColumnName("chapter_index");
        item.Property(i => i.ChapterCaption).HasColumnName("chapter_caption");
        item.Property(i => i.Number).HasColumnName("number").HasMaxLength(ConjunctureItem.NumberLength);
        item.Property(i => i.Caption).HasColumnName("caption");
        item.Property(i => i.Units).HasColumnName("units").HasMaxLength(ConjunctureItem.UnitLength);
        item.Property(i => i.IsInactive).HasColumnName("is_inactive");
        item.Property(i => i.Selection).HasColumnName("selection").HasConversion<string>().HasMaxLength(16);
        item.Property(i => i.SelectedOffer).HasColumnName("selected_offer");
        item.Property(i => i.SelectedPrice).HasColumnName("selected_price").HasPrecision(18, 2);
        item.HasIndex(i => new { i.AnalysisId, i.Order }).IsUnique().HasDatabaseName("ux_conjuncture_items_analysis_order");
        item.HasMany(i => i.Offers).WithOne().HasForeignKey(o => o.ItemId).OnDelete(DeleteBehavior.Cascade);

        var offer = builder.Entity<ConjunctureOffer>();
        offer.ToTable("conjuncture_offers");
        offer.HasKey(o => o.Id);
        offer.Property(o => o.Id).HasColumnName("id");
        offer.Property(o => o.ItemId).HasColumnName("item_id");
        offer.Property(o => o.Order).HasColumnName("order");
        offer.Property(o => o.Number).HasColumnName("number").HasMaxLength(ConjunctureItem.NumberLength);
        offer.Property(o => o.Caption).HasColumnName("caption");
        offer.Property(o => o.Units).HasColumnName("units").HasMaxLength(ConjunctureOffer.UnitLength);
        offer.Property(o => o.ResourceCode).HasColumnName("resource_code").HasMaxLength(ConjunctureOffer.CodeLength);
        offer.Property(o => o.SupplierName).HasColumnName("supplier_name");
        offer.Property(o => o.SupplierInn).HasColumnName("supplier_inn").HasMaxLength(ConjunctureOffer.InnLength);
        offer.Property(o => o.Price).HasColumnName("price").HasPrecision(18, 2);
        offer.Property(o => o.Vat).HasColumnName("vat").HasPrecision(9, 4);
        offer.Property(o => o.PriceWithVat).HasColumnName("price_with_vat").HasPrecision(18, 2);
        offer.Property(o => o.OfferDate).HasColumnName("offer_date");
        offer.HasIndex(o => new { o.ItemId, o.Order }).IsUnique().HasDatabaseName("ux_conjuncture_offers_item_order");

        var statement = builder.Entity<VolumeStatement>();
        statement.ToTable("volume_statements");
        Attachment(statement, "volume_statements", VolumeImportKey);
        statement.HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.StatementId).OnDelete(DeleteBehavior.Cascade);

        var line = builder.Entity<VolumeStatementLine>();
        line.ToTable("volume_statement_lines");
        line.HasKey(l => l.Id);
        line.Property(l => l.Id).HasColumnName("id");
        line.Property(l => l.StatementId).HasColumnName("statement_id");
        line.Property(l => l.Order).HasColumnName("order");
        line.Property(l => l.ChapterIndex).HasColumnName("chapter_index");
        line.Property(l => l.ChapterCaption).HasColumnName("chapter_caption");
        line.Property(l => l.Number).HasColumnName("number").HasMaxLength(VolumeStatementLine.NumberLength);
        line.Property(l => l.Caption).HasColumnName("caption");
        line.Property(l => l.Code).HasColumnName("code").HasMaxLength(VolumeStatementLine.CodeLength);
        line.Property(l => l.Units).HasColumnName("units").HasMaxLength(VolumeStatementLine.UnitLength);
        line.Property(l => l.Quantity).HasColumnName("quantity").HasPrecision(18, 6);
        line.Property(l => l.Formula).HasColumnName("formula");
        line.Property(l => l.Drawings).HasColumnName("drawings");
        line.Property(l => l.Note).HasColumnName("note");
        line.Property(l => l.IsSubordinate).HasColumnName("is_subordinate");
        line.HasIndex(l => new { l.StatementId, l.Order }).IsUnique().HasDatabaseName("ux_volume_statement_lines_statement_order");
    }

    /// <summary>Общие колонки приложения — одним местом на обе таблицы.</summary>
    private static void Attachment<T>(EntityTypeBuilder<T> e, string table, string importKey) where T : EstimateAttachment
    {
        e.HasKey(a => a.Id);
        e.Property(a => a.Id).HasColumnName("id");
        e.Property(a => a.EstimateId).HasColumnName("estimate_id");
        e.Property(a => a.ImportKey).HasColumnName("import_key");
        e.Property(a => a.BlobPath).HasColumnName("blob_path");
        e.Property(a => a.FileName).HasColumnName("file_name").HasMaxLength(EstimateImport.FileNameLength);
        e.Property(a => a.ProgramVersion).HasColumnName("program_version").HasMaxLength(EstimateImport.VersionLength);
        e.Property(a => a.SchemaVersion).HasColumnName("schema_version").HasMaxLength(EstimateImport.VersionLength);
        e.Property(a => a.Number).HasColumnName("number").HasMaxLength(EstimateAttachment.NumberLength);
        e.Property(a => a.Name).HasColumnName("name").HasMaxLength(EstimateAttachment.NameLength);
        e.Property(a => a.CreationDate).HasColumnName("creation_date");
        e.Property(a => a.ParsedPositions).HasColumnName("parsed_positions");
        e.Property(a => a.PositionsInFile).HasColumnName("positions_in_file");
        e.Property(a => a.ImportedAt).HasColumnName("imported_at");
        e.Property(a => a.ImportedBy).HasColumnName("imported_by");
        e.Property(a => a.ImportedByName).HasColumnName("imported_by_name");
        e.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();
        e.HasOne<Estimate>().WithMany().HasForeignKey(a => a.EstimateId).OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(a => a.EstimateId).HasDatabaseName($"ix_{table}_estimate_id");
        e.HasIndex(a => a.ImportKey).IsUnique().HasDatabaseName(importKey);
    }
}
