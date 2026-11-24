using BHS.CRG.Modules.Plan.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Schedules;

internal static class ScheduleModel
{
    internal static void ConfigureLinks(ModelBuilder builder)
    {
        var link = builder.Entity<ScheduleLink>();
        link.ToTable("schedule_links");
        link.HasKey(l => l.Id);
        link.Property(l => l.Id).HasColumnName("id");
        link.Property(l => l.PredecessorId).HasColumnName("predecessor_id");
        link.Property(l => l.SuccessorId).HasColumnName("successor_id");
        link.Property(l => l.Kind).HasColumnName("kind").HasMaxLength(2);
        link.Property(l => l.Lag).HasColumnName("lag");
        link.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();
        link.HasOne<ScheduleWork>().WithMany().HasForeignKey(l => l.PredecessorId).OnDelete(DeleteBehavior.Cascade);
        link.HasOne<ScheduleWork>().WithMany().HasForeignKey(l => l.SuccessorId).OnDelete(DeleteBehavior.Cascade);
        link.HasIndex(l => new { l.PredecessorId, l.SuccessorId }).IsUnique();
    }

    public static void Configure(ModelBuilder builder)
    {
        var schedule = builder.Entity<Schedule>();
        schedule.ToTable("schedules");
        schedule.HasKey(s => s.Id);
        schedule.Property(s => s.Id).HasColumnName("id");
        schedule.Property(s => s.ConstructionId).HasColumnName("construction_id");
        schedule.Property(s => s.RevisionId).HasColumnName("revision_id");
        schedule.Property(s => s.CreationKey).HasColumnName("creation_key");
        schedule.Property(s => s.Name).HasColumnName("name").HasMaxLength(Schedule.NameLength);
        schedule.Property(s => s.DistributionFingerprint).HasColumnName("distribution_fingerprint").HasMaxLength(64);
        schedule.Property(s => s.Unmapped).HasColumnName("unmapped");
        schedule.Property(s => s.CreatedAt).HasColumnName("created_at");
        schedule.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        schedule.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();
        schedule.HasIndex(s => new { s.ConstructionId, s.CreationKey }).IsUnique();
        schedule.HasOne<EstimateRevision>().WithMany().HasForeignKey(s => s.RevisionId).OnDelete(DeleteBehavior.Restrict);

        var work = builder.Entity<ScheduleWork>();
        work.ToTable("schedule_works");
        work.HasKey(w => w.Id);
        work.Property(w => w.Id).HasColumnName("id");
        work.Property(w => w.ScheduleId).HasColumnName("schedule_id");
        work.Property(w => w.PlanItemId).HasColumnName("plan_item_id");
        work.Property(w => w.SourceRowId).HasColumnName("source_row_id");
        work.Property(w => w.SectionId).HasColumnName("section_id");
        work.Property(w => w.Name).HasColumnName("name");
        work.Property(w => w.Volume).HasColumnName("volume").HasPrecision(18, 6);
        work.Property(w => w.Start).HasColumnName("starts_on");
        work.Property(w => w.End).HasColumnName("ends_on");
        work.Property(w => w.UpdatedAt).HasColumnName("updated_at");
        work.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();
        work.HasOne<Schedule>().WithMany().HasForeignKey(w => w.ScheduleId).OnDelete(DeleteBehavior.Cascade);
        work.HasOne<EstimateRow>().WithMany().HasForeignKey(w => w.SourceRowId).OnDelete(DeleteBehavior.Restrict);
        work.HasIndex(w => w.PlanItemId);
        work.HasIndex(w => w.SectionId);
        work.HasIndex(w => new { w.ScheduleId, w.SourceRowId, w.SectionId }).IsUnique()
            .HasFilter("source_row_id IS NOT NULL").AreNullsDistinct(false);

        var assignee = builder.Entity<ScheduleWorkAssignee>();
        assignee.ToTable("schedule_work_assignees");
        assignee.HasKey(a => new { a.WorkId, a.EmployeeId });
        assignee.Property(a => a.WorkId).HasColumnName("work_id");
        assignee.Property(a => a.EmployeeId).HasColumnName("employee_id");
        assignee.HasOne<ScheduleWork>().WithMany().HasForeignKey(a => a.WorkId).OnDelete(DeleteBehavior.Cascade);
        assignee.HasIndex(a => a.EmployeeId);
    }
}
