using BHS.CRG.Modules.Data;
using BHS.CRG.Modules.Work.Data;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Work.Crew;

internal sealed class CrewGroupRecord : IIsolatedRecord
{
    public Guid Id { get; private init; }
    public Guid ConstructionId { get; private init; }
    public DateOnly Date { get; private init; }
    public ReportShift Shift { get; private init; }
    public Guid WorkTypeId { get; private init; }
    public Guid InputRevision { get; private set; }
    public Guid? ApprovedRevision { get; private set; }
    public decimal? Total { get; private set; }
    public Guid? UnitId { get; private set; }
    public Guid? ApprovedBy { get; private set; }
    public string? ApprovedByName { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public uint Version { get; private set; }

    internal CrewShiftKey ShiftKey => new(ConstructionId, Date, Shift);

    public string State => ApprovedRevision is null ? "pending" : ApprovedRevision == InputRevision ? "approved" : "stale";

    public static CrewGroupRecord Create(Report report, Guid workTypeId) => new()
    {
        Id = Guid.NewGuid(), ConstructionId = report.ConstructionId, Date = report.Date,
        Shift = report.Shift, WorkTypeId = workTypeId, InputRevision = Guid.NewGuid(),
    };

    public void InputsChanged() => InputRevision = Guid.NewGuid();

    public void Approve(decimal total, Guid unitId, ReportJournalActor actor, DateTimeOffset now)
    {
        Total = total;
        UnitId = unitId;
        ApprovedBy = actor.UserId;
        ApprovedByName = actor.Name;
        ApprovedAt = now;
        ApprovedRevision = InputRevision;
    }

    public static void Configure(ModelBuilder builder)
    {
        var group = builder.Entity<CrewGroupRecord>();
        group.ToTable("crew_groups");
        group.HasKey(g => g.Id);
        group.HasIndex(g => new { g.ConstructionId, g.Date, g.Shift, g.WorkTypeId }).IsUnique();
        group.HasIndex(g => g.WorkTypeId);
        group.HasIndex(g => g.UnitId);
        group.Property(g => g.Id).HasColumnName("id").ValueGeneratedNever();
        group.Property(g => g.ConstructionId).HasColumnName("construction_id");
        group.Property(g => g.Date).HasColumnName("date");
        group.Property(g => g.Shift).HasColumnName("shift").HasMaxLength(8)
            .HasConversion(v => Report.ShiftCode(v), v => Report.ParseShift(v));
        group.Property(g => g.WorkTypeId).HasColumnName("work_type_id");
        group.Property(g => g.InputRevision).HasColumnName("input_revision");
        group.Property(g => g.ApprovedRevision).HasColumnName("approved_revision");
        group.Property(g => g.Total).HasColumnName("total").HasPrecision(18, 4);
        group.Property(g => g.UnitId).HasColumnName("unit_id");
        group.Property(g => g.ApprovedBy).HasColumnName("approved_by");
        group.Property(g => g.ApprovedByName).HasColumnName("approved_by_name");
        group.Property(g => g.ApprovedAt).HasColumnName("approved_at");
        group.Property(g => g.Version).IsRowVersion();
    }
}
