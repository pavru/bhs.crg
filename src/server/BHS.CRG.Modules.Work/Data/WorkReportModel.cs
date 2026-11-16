using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Таблицы отчёта монтажника в модели <see cref="WorkDbContext" /> (change <c>work-13-report-model</c>): отчёт,
/// строки, материалы, участники и протокол изменений. Отдельным файлом — чтобы контекст не рос с каждой записью.
///
/// <para>⚠️ <b>Наборов (<c>DbSet</c>) у этих записей нет</b>: все изолированные (<c>IIsolatedRecord</c>), и
/// читаются только в файле чтения с параметром доступа. Навигации — только между ними самими: от отчёта к строкам и
/// от строки к материалам и участникам. Навигация от общей сущности к отчёту читала бы его без охвата
/// (сторож — <c>IsolatedReadInventoryTests</c>).</para>
///
/// <para>Индексы по держащим колонкам — под вопрос ядра «кто держит запись» при её удалении (CORE-34.1): без них
/// удаление вида работы проходило бы все строки отчётов (сторож — <c>ModuleReferenceInventoryTests</c>).</para>
/// </summary>
internal static class WorkReportModel
{
    public static void Configure(ModelBuilder builder, string rowVersion)
    {
        builder.HasSequence<long>(WorkDbContext.ReportSequence, WorkDbContext.SchemaName);

        var report = builder.Entity<Report>();
        report.ToTable("reports");
        report.HasKey(r => r.Id);
        report.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        report.Property(r => r.EmployeeId).HasColumnName("employee_id");
        report.Property(r => r.ConstructionId).HasColumnName("construction_id");
        report.Property(r => r.Date).HasColumnName("date");
        report.Property(r => r.Shift).HasColumnName("shift").HasMaxLength(8)
            .HasConversion(v => Report.ShiftCode(v), v => Report.ParseShift(v));
        report.Property(r => r.Hours).HasColumnName("hours").HasPrecision(5, 2);
        report.Property(r => r.Status).HasColumnName("status").HasMaxLength(16)
            .HasConversion(v => Report.StatusCode(v), v => Enum.Parse<ReportStatus>(v, true));
        report.Property(r => r.Note).HasColumnName("note");
        report.Property(r => r.Seq).HasColumnName("seq");
        report.Property(r => r.CreatedAt).HasColumnName("created_at");
        report.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        report.Property(r => r.EnteredByUserId).HasColumnName("entered_by_user_id");
        report.Property(r => r.EnteredByName).HasColumnName("entered_by_name").HasMaxLength(256);
        report.Property(r => r.SubmittedAt).HasColumnName("submitted_at");
        report.Property(r => r.WindowEndsAt).HasColumnName("window_ends_at");
        report.Property(r => r.SubmittedBy).HasColumnName("submitted_by").HasMaxLength(8)
            .HasConversion(v => v == null ? null : Report.SubmitterCode(v.Value),
                v => v == null ? null : Enum.Parse<ReportSubmitter>(v, true));
        report.Property(r => r.RequiredMissing).HasColumnName("required_missing").HasColumnType("jsonb");
        report.Property<uint>(rowVersion).IsRowVersion();
        report.ToTable(t =>
        {
            t.HasCheckConstraint("ck_reports_hours", $"hours IS NULL OR (hours >= 0 AND hours <= {Report.MaxHours:0})");
            t.HasCheckConstraint("ck_reports_shift", "shift IN ('day', 'night')");
        });
        report.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.ReportId).OnDelete(DeleteBehavior.Cascade);

        // Ключ отчёта (ТЗ WORK-4, MOB-35): без смены ночная работа затирала бы дневную. Сотрудник первым — он же
        // держащая колонка, и «кто держит сотрудника» идёт по этому индексу.
        report.HasIndex(r => new { r.EmployeeId, r.ConstructionId, r.Date, r.Shift }).IsUnique().HasDatabaseName("ux_reports_key");
        // Черновики с истёкшим окном — запрос автоподачи (work-15, ТЗ WORK-10).
        report.HasIndex(r => new { r.Status, r.WindowEndsAt }).HasDatabaseName("ix_reports_window");
        // Отчёты стройки по дате — приёмка и «кто держит стройку».
        report.HasIndex(r => new { r.ConstructionId, r.Date }).HasDatabaseName("ix_reports_site");

        var line = builder.Entity<ReportLine>();
        line.ToTable("report_lines");
        line.HasKey(l => l.Id);
        line.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
        line.Property(l => l.ReportId).HasColumnName("report_id");
        line.Property(l => l.Position).HasColumnName("position");
        line.Property(l => l.WorkTypeId).HasColumnName("work_type_id");
        line.Property(l => l.VariantId).HasColumnName("variant_id");
        line.Property(l => l.Volume).HasColumnName("volume").HasPrecision(18, 4);
        line.Property(l => l.UnitId).HasColumnName("unit_id");
        line.Property(l => l.SectionId).HasColumnName("section_id");
        line.Property(l => l.Location).HasColumnName("location");
        line.Property(l => l.Comment).HasColumnName("comment");
        line.Property(l => l.IsCrew).HasColumnName("is_crew");
        line.Property(l => l.WorkPlanItemId).HasColumnName("work_plan_item_id");
        line.Property(l => l.Details).HasColumnName("details").HasColumnType("jsonb");
        line.HasMany(l => l.Materials).WithOne().HasForeignKey(m => m.LineId).OnDelete(DeleteBehavior.Cascade);
        line.HasMany(l => l.Participants).WithOne().HasForeignKey(p => p.LineId).OnDelete(DeleteBehavior.Cascade);
        line.HasIndex(l => new { l.ReportId, l.Position }).HasDatabaseName("ix_report_lines_report");
        line.HasIndex(l => l.WorkTypeId).HasDatabaseName("ix_report_lines_work_type");
        line.HasIndex(l => l.VariantId).HasDatabaseName("ix_report_lines_variant");
        line.HasIndex(l => l.UnitId).HasDatabaseName("ix_report_lines_unit");
        line.HasIndex(l => l.SectionId).HasDatabaseName("ix_report_lines_section");
        line.HasIndex(l => l.WorkPlanItemId).HasDatabaseName("ix_report_lines_plan_item");

        var material = builder.Entity<ReportLineMaterial>();
        material.ToTable("report_line_materials");
        material.HasKey(m => m.Id);
        material.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
        material.Property(m => m.LineId).HasColumnName("line_id");
        material.Property(m => m.Position).HasColumnName("position");
        material.Property(m => m.NomenclatureId).HasColumnName("nomenclature_id");
        material.Property(m => m.Note).HasColumnName("note");
        material.HasIndex(m => new { m.LineId, m.Position }).HasDatabaseName("ix_report_line_materials_line");
        material.HasIndex(m => m.NomenclatureId).HasDatabaseName("ix_report_line_materials_nomenclature");

        var participant = builder.Entity<ReportLineParticipant>();
        participant.ToTable("report_line_participants");
        participant.HasKey(p => p.Id);
        participant.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        participant.Property(p => p.LineId).HasColumnName("line_id");
        participant.Property(p => p.EmployeeId).HasColumnName("employee_id");
        participant.HasIndex(p => p.LineId).HasDatabaseName("ix_report_line_participants_line");
        participant.HasIndex(p => p.EmployeeId).HasDatabaseName("ix_report_line_participants_employee");

        var change = builder.Entity<ReportChange>();
        change.ToTable("report_changes");
        change.HasKey(c => c.Seq);
        change.Property(c => c.Seq).HasColumnName("seq").ValueGeneratedNever();
        change.Property(c => c.ReportId).HasColumnName("report_id");
        change.Property(c => c.EmployeeId).HasColumnName("employee_id");
        change.Property(c => c.ConstructionId).HasColumnName("construction_id");
        change.Property(c => c.ChangeId).HasColumnName("change_id");
        change.Property(c => c.Kind).HasColumnName("kind").HasMaxLength(16)
            .HasConversion(v => ReportChange.KindCode(v), v => Enum.Parse<ReportChangeKind>(v, true));
        change.Property(c => c.DeviceTime).HasColumnName("device_time");
        change.Property(c => c.ServerTime).HasColumnName("server_time");
        change.Property(c => c.Version).HasColumnName("version").HasMaxLength(16);
        // Повтор правки (ТЗ MOB-15): «применён ли этот changeId у этого отчёта» — один вопрос по индексу, и
        // второе применение того же изменения база не примет, даже если два повтора пришли разом.
        change.HasIndex(c => new { c.ReportId, c.ChangeId }).IsUnique().HasDatabaseName("ux_report_changes_change");
        // Курсор «свои изменения после N» (ТЗ MOB-16).
        change.HasIndex(c => new { c.EmployeeId, c.Seq }).HasDatabaseName("ix_report_changes_employee");
        change.HasIndex(c => new { c.ConstructionId, c.Seq }).HasDatabaseName("ix_report_changes_site");

        // Журнал отчёта (work-14): только дописывается, ссылка на отчёт — без внешнего ключа, как у протокола.
        var journal = builder.Entity<ReportJournalEntry>();
        journal.ToTable("report_journal");
        journal.HasKey(e => e.Id);
        journal.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
        journal.Property(e => e.ReportId).HasColumnName("report_id");
        journal.Property(e => e.ChangeId).HasColumnName("change_id");
        journal.Property(e => e.Seq).HasColumnName("seq");
        journal.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(32);
        journal.Property(e => e.AuthorId).HasColumnName("author_id");
        journal.Property(e => e.AuthorName).HasColumnName("author_name").HasMaxLength(256);
        journal.Property(e => e.ExecutorEmployeeId).HasColumnName("executor_employee_id");
        journal.Property(e => e.ExecutorName).HasColumnName("executor_name").HasMaxLength(256);
        journal.Property(e => e.EnteredFor).HasColumnName("entered_for");
        journal.Property(e => e.DeviceTime).HasColumnName("device_time");
        journal.Property(e => e.ServerTime).HasColumnName("server_time");
        journal.Property(e => e.AfterSubmission).HasColumnName("after_submission");
        journal.Property(e => e.Changes).HasColumnName("changes").HasColumnType("jsonb");
        journal.Property(e => e.Comment).HasColumnName("comment").HasMaxLength(ReportRules.CommentMax);
        // Журнал одного отчёта по номеру — адрес чтения; одна запись на номер — как у протокола.
        journal.HasIndex(e => new { e.ReportId, e.Seq }).IsUnique().HasDatabaseName("ux_report_journal_report_seq");

        // Отметка сводки о поданных (work-16): отдельной таблицей, а не колонкой отчёта — запись колонки сдвинула бы
        // версию строки отчёта (xmin), и правка по только что прочитанной версии получила бы 409 без видимой правки.
        var digest = builder.Entity<ReviewDigestMark>();
        digest.ToTable("review_digests");
        digest.HasKey(d => new { d.ReportId, d.SubmittedAt });
        digest.Property(d => d.ReportId).HasColumnName("report_id");
        digest.Property(d => d.SubmittedAt).HasColumnName("submitted_at");
        digest.Property(d => d.DigestedAt).HasColumnName("digested_at");
        // Удаляется только черновик, а в сводку входит поданный; каскад — на случай удаления мимо пути записи.
        digest.HasOne<Report>().WithMany().HasForeignKey(d => d.ReportId).OnDelete(DeleteBehavior.Cascade);
    }
}
