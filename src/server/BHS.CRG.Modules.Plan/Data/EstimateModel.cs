using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Раскладка таблиц сметы (change <c>plan-06-estimate-model</c>). Вручную, а не соглашениями EF: имена
/// таблиц и колонок — то, что увидит человек в отчёте резервной копии и в запросе к базе, как у
/// <c>costs</c>.
///
/// <para><b>Ссылки на записи ядра</b> (стройка, раздел, вид работы, единица, позиция перечня,
/// номенклатура) — идентификаторами без внешних ключей: схема модуля со схемой ядра ключами не
/// связывается (ТЗ CORE-34.1). Держателей находит скан ядра, слова — <see cref="PlanReferences" />, и
/// поэтому у каждой держащей колонки есть индекс, где она первая: вопрос «кто держит» задаётся при
/// каждом удалении записи справочника.</para>
///
/// <para><b>Деньги</b> — <c>numeric(18,2)</c>, а не <c>double</c>: копейка, потерянная двоичной дробью,
/// расходится со сметой в файле. Объёмы и количества — <c>numeric(18,6)</c>: сметные объёмы бывают
/// «0,0035 т».</para>
/// </summary>
internal static class EstimateModel
{
    public static void Configure(ModelBuilder builder)
    {
        var estimate = builder.Entity<Estimate>();
        estimate.ToTable("estimates");
        estimate.HasKey(e => e.Id);
        estimate.Property(e => e.Id).HasColumnName("id");
        estimate.Property(e => e.ConstructionId).HasColumnName("construction_id");
        estimate.Property(e => e.SectionId).HasColumnName("section_id");
        estimate.Property(e => e.Name).HasColumnName("name").HasMaxLength(Estimate.NameLength);
        estimate.Property(e => e.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        estimate.Property(e => e.CurrentRevisionId).HasColumnName("current_revision_id");
        estimate.Property(e => e.CreatedAt).HasColumnName("created_at");
        estimate.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();
        estimate.HasIndex(e => e.ConstructionId).HasDatabaseName("ix_estimates_construction_id");
        estimate.HasIndex(e => e.SectionId).HasDatabaseName("ix_estimates_section_id").HasFilter("section_id IS NOT NULL");

        // Действующая редакция держится базой: редакцию, которая действует, не удалить и запросу, чья
        // проверка прошла раньше смены действующей (PLAN-5.2). Без каскада и без обнуления — отказ.
        estimate.HasOne<EstimateRevision>().WithMany().HasForeignKey(e => e.CurrentRevisionId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName(PlanDbContext.CurrentRevisionKey);
        estimate.HasIndex(e => e.CurrentRevisionId).HasDatabaseName("ix_estimates_current_revision_id");

        var revision = builder.Entity<EstimateRevision>();
        revision.ToTable("estimate_revisions");
        revision.HasKey(r => r.Id);
        revision.Property(r => r.Id).HasColumnName("id");
        revision.Property(r => r.EstimateId).HasColumnName("estimate_id");
        revision.Property(r => r.Number).HasColumnName("number");
        revision.Property(r => r.SourceBlobPath).HasColumnName("source_blob_path");
        revision.Property(r => r.SourceFileName).HasColumnName("source_file_name").HasMaxLength(EstimateRevision.FileNameLength);
        revision.Property(r => r.SchemaVersion).HasColumnName("schema_version").HasMaxLength(EstimateRevision.SchemaVersionLength);
        revision.Property(r => r.NormativeBase).HasColumnName("normative_base").HasMaxLength(EstimateRevision.NormativeBaseLength);
        revision.Property(r => r.MatchedAt).HasColumnName("matched_at");
        revision.Property(r => r.ImportKey).HasColumnName("import_key").HasMaxLength(EstimateRevision.ImportKeyLength);
        revision.Property(r => r.ImportedAt).HasColumnName("imported_at");
        revision.Property(r => r.ImportedBy).HasColumnName("imported_by");
        revision.Property(r => r.ImportedByName).HasColumnName("imported_by_name");
        revision.Property(r => r.FrozenAt).HasColumnName("frozen_at");
        revision.Property(r => r.FrozenCode).HasColumnName("frozen_code").HasMaxLength(EstimateRevision.FreezeCodeLength);
        revision.Property(r => r.FrozenLabel).HasColumnName("frozen_label").HasMaxLength(EstimateRevision.FreezeLabelLength);
        revision.Ignore(r => r.Frozen);
        revision.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();

        // Смету с редакциями база удалить не даст — удаляется только пустая (PLAN-2): иначе вместе с ней
        // ушли бы редакции, из которых, быть может, уже сделаны КП и график.
        revision.HasOne<Estimate>().WithMany().HasForeignKey(r => r.EstimateId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName(PlanDbContext.RevisionEstimateKey);
        // Номер растёт в смете; два импорта одновременно получат один номер — и второй отказ, а не дубль.
        revision.HasIndex(r => new { r.EstimateId, r.Number }).IsUnique().HasDatabaseName(PlanDbContext.RevisionNumberKey);
        // Ключ импорта единствен (plan-07): двойное создание редакции из одного импорта даёт одну редакцию —
        // вторая запись проигрывает здесь, а не заводит дубль, который потом не отличить от прежней.
        revision.HasIndex(r => r.ImportKey).IsUnique().HasDatabaseName(PlanDbContext.RevisionImportKey)
            .HasFilter("import_key IS NOT NULL");

        var row = builder.Entity<EstimateRow>();
        row.ToTable("estimate_rows");
        row.HasKey(r => r.Id);
        row.Property(r => r.Id).HasColumnName("id");
        row.Property(r => r.RevisionId).HasColumnName("revision_id");
        row.Property(r => r.Order).HasColumnName("order");
        row.Property(r => r.LsrSectionCode).HasColumnName("lsr_section_code").HasMaxLength(EstimateRow.SectionCodeLength);
        row.Property(r => r.LsrSectionName).HasColumnName("lsr_section_name").HasMaxLength(EstimateRow.SectionNameLength);
        row.Property(r => r.NumberInSection).HasColumnName("number_in_section").HasMaxLength(EstimateRow.NumberLength);
        row.Property(r => r.RateCode).HasColumnName("rate_code").HasMaxLength(EstimateRow.RateCodeLength);
        row.Property(r => r.Name).HasColumnName("name");
        row.Property(r => r.SourceVolume).HasColumnName("source_volume").HasPrecision(18, 6);
        row.Property(r => r.SourceUnit).HasColumnName("source_unit").HasMaxLength(EstimateRow.UnitLength);
        row.Property(r => r.WorkTypeId).HasColumnName("work_type_id");
        row.Property(r => r.UnitId).HasColumnName("unit_id");
        row.Property(r => r.Factor).HasColumnName("factor").HasPrecision(18, 6);
        row.Property(r => r.Volume).HasColumnName("volume").HasPrecision(18, 6);
        row.Property(r => r.PlanItemId).HasColumnName("plan_item_id");
        row.Property(r => r.RateMatchId).HasColumnName("rate_match_id");
        row.Property(r => r.UnitCost).HasColumnName("unit_cost").HasPrecision(18, 2);
        row.Property(r => r.Total).HasColumnName("total").HasPrecision(18, 2);
        row.Property(r => r.Data).HasColumnName("data").HasColumnType("jsonb");
        row.Ignore(r => r.Unmatched);
        row.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();
        // Строки удаляются вместе с редакцией: отдельно от неё они не живут.
        row.HasOne<EstimateRevision>().WithMany().HasForeignKey(r => r.RevisionId).OnDelete(DeleteBehavior.Cascade);
        // Порядок — ключ страниц чтения строк (курсор), поэтому он единственен в редакции.
        row.HasIndex(r => new { r.RevisionId, r.Order }).IsUnique().HasDatabaseName("ix_estimate_rows_revision_order");
        row.HasIndex(r => r.WorkTypeId).HasDatabaseName("ix_estimate_rows_work_type_id").HasFilter("work_type_id IS NOT NULL");
        row.HasIndex(r => r.UnitId).HasDatabaseName("ix_estimate_rows_unit_id").HasFilter("unit_id IS NOT NULL");
        row.HasIndex(r => r.PlanItemId).HasDatabaseName("ix_estimate_rows_plan_item_id").HasFilter("plan_item_id IS NOT NULL");

        var material = builder.Entity<EstimateRowMaterial>();
        material.ToTable("estimate_row_materials");
        material.HasKey(m => m.Id);
        material.Property(m => m.Id).HasColumnName("id");
        material.Property(m => m.RowId).HasColumnName("row_id");
        material.Property(m => m.Order).HasColumnName("order");
        material.Property(m => m.NomenclatureId).HasColumnName("nomenclature_id");
        material.Property(m => m.SourceName).HasColumnName("source_name");
        material.Property(m => m.SourceCode).HasColumnName("source_code").HasMaxLength(EstimateRowMaterial.SourceCodeLength);
        material.Property(m => m.SourceUnit).HasColumnName("source_unit").HasMaxLength(EstimateRowMaterial.SourceUnitLength);
        material.Property(m => m.Quantity).HasColumnName("quantity").HasPrecision(18, 6);
        material.Property(m => m.UnitPrice).HasColumnName("unit_price").HasPrecision(18, 2);
        material.Property(m => m.Amount).HasColumnName("amount").HasPrecision(18, 2);
        material.Property(m => m.ResourceMatchId).HasColumnName("resource_match_id");
        material.Ignore(m => m.Unmatched);
        material.HasOne<EstimateRow>().WithMany().HasForeignKey(m => m.RowId).OnDelete(DeleteBehavior.Cascade);
        material.HasIndex(m => m.RowId).HasDatabaseName("ix_estimate_row_materials_row_id");
        material.HasIndex(m => m.NomenclatureId).HasDatabaseName("ix_estimate_row_materials_nomenclature_id")
            .HasFilter("nomenclature_id IS NOT NULL");

        var labor = builder.Entity<EstimateRowLabor>();
        labor.ToTable("estimate_row_labor");
        labor.HasKey(l => l.Id);
        labor.Property(l => l.Id).HasColumnName("id");
        labor.Property(l => l.RowId).HasColumnName("row_id");
        labor.Property(l => l.Order).HasColumnName("order");
        labor.Property(l => l.ManHours).HasColumnName("man_hours").HasPrecision(18, 4);
        labor.HasOne<EstimateRow>().WithMany().HasForeignKey(l => l.RowId).OnDelete(DeleteBehavior.Cascade);
        labor.HasIndex(l => l.RowId).HasDatabaseName("ix_estimate_row_labor_row_id");

        ConfigureImport(builder);
        RateMatch.Map(builder);
        ResourceMatch.Map(builder);
    }

    /// <summary>
    /// Запись импорта (change <c>plan-07-estimate-import</c>). Смета и редакция — свои записи модуля, ключами:
    /// импорт в смету уходит вместе с удалённой сметой (удаляется только смета без редакций, то есть импорт
    /// её ещё не наполнил), а отчёт созданной редакции — вместе с редакцией.
    /// </summary>
    private static void ConfigureImport(ModelBuilder builder)
    {
        var import = builder.Entity<EstimateImport>();
        import.ToTable("estimate_imports");
        import.HasKey(i => i.Id);
        import.Property(i => i.Id).HasColumnName("id");
        import.Property(i => i.ConstructionId).HasColumnName("construction_id");
        import.Property(i => i.EstimateId).HasColumnName("estimate_id");
        import.Property(i => i.NewEstimateName).HasColumnName("new_estimate_name").HasMaxLength(Estimate.NameLength);
        import.Property(i => i.NewEstimateSectionId).HasColumnName("new_estimate_section_id");
        import.Property(i => i.NewEstimateKind).HasColumnName("new_estimate_kind").HasConversion<string>().HasMaxLength(16);
        import.Property(i => i.BlobPath).HasColumnName("blob_path");
        import.Property(i => i.FileName).HasColumnName("file_name").HasMaxLength(EstimateImport.FileNameLength);
        import.Property(i => i.State).HasColumnName("state").HasConversion<string>().HasMaxLength(16);
        import.Property(i => i.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
        import.Property(i => i.ProgramVersion).HasColumnName("program_version").HasMaxLength(EstimateImport.VersionLength);
        import.Property(i => i.SchemaVersion).HasColumnName("schema_version").HasMaxLength(EstimateImport.VersionLength);
        import.Property(i => i.LsrNumber).HasColumnName("lsr_number").HasMaxLength(EstimateImport.LsrNumberLength);
        import.Property(i => i.LsrName).HasColumnName("lsr_name").HasMaxLength(EstimateImport.LsrNameLength);
        import.Property(i => i.Parsed).HasColumnName("parsed").HasColumnType("jsonb");
        import.Property(i => i.Unparsed).HasColumnName("unparsed").HasColumnType("jsonb");
        import.Property(i => i.ParsedPositions).HasColumnName("parsed_positions");
        import.Property(i => i.PositionsInFile).HasColumnName("positions_in_file");
        import.Property(i => i.RefusalCode).HasColumnName("refusal_code").HasMaxLength(EstimateImport.RefusalCodeLength);
        import.Property(i => i.RefusalText).HasColumnName("refusal_text");
        import.Property(i => i.JobId).HasColumnName("job_id");
        import.Property(i => i.RevisionId).HasColumnName("revision_id");
        import.Property(i => i.CreatedAt).HasColumnName("created_at");
        import.Property(i => i.CreatedBy).HasColumnName("created_by");
        import.Property(i => i.CreatedByName).HasColumnName("created_by_name");
        import.Property(i => i.ParsedAt).HasColumnName("parsed_at");
        import.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();

        import.HasOne<Estimate>().WithMany().HasForeignKey(i => i.EstimateId).OnDelete(DeleteBehavior.Cascade);
        import.HasOne<EstimateRevision>().WithMany().HasForeignKey(i => i.RevisionId).OnDelete(DeleteBehavior.Cascade);
        import.HasIndex(i => i.EstimateId).HasDatabaseName("ix_estimate_imports_estimate_id").HasFilter("estimate_id IS NOT NULL");
        import.HasIndex(i => i.RevisionId).HasDatabaseName("ix_estimate_imports_revision_id").HasFilter("revision_id IS NOT NULL");
        import.HasIndex(i => i.ConstructionId).HasDatabaseName("ix_estimate_imports_construction_id");
        import.HasIndex(i => i.NewEstimateSectionId).HasDatabaseName("ix_estimate_imports_new_estimate_section_id")
            .HasFilter("new_estimate_section_id IS NOT NULL");
    }
}
