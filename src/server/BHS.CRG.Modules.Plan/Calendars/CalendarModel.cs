using BHS.CRG.Modules.Plan.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BHS.CRG.Modules.Plan.Calendars;

internal static class CalendarModel
{
    public const string YearKey = "pk_calendar_years";
    public const string DaysKey = "pk_calendar_days";
    public const string ConstructionKey = "pk_construction_calendars";
    public const string ConstructionDaysKey = "pk_construction_calendar_days";

    public static void Configure(ModelBuilder builder)
    {
        var year = builder.Entity<CalendarYear>();
        year.ToTable("calendar_years");
        year.HasKey(c => c.Year).HasName(YearKey);
        year.Property(c => c.Year).HasColumnName("year").ValueGeneratedNever();
        year.Property(c => c.WorkingWeek).HasColumnName("working_week");
        year.Property(c => c.HoursPerShift).HasColumnName("hours_per_shift").HasPrecision(4, 2);
        year.Property(c => c.ImportFileName).HasColumnName("import_file_name").HasMaxLength(255);
        year.Property(c => c.ImportedAt).HasColumnName("imported_at");
        year.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();
        year.HasMany(c => c.Days).WithOne().HasForeignKey(d => d.Year).OnDelete(DeleteBehavior.Cascade);

        var day = builder.Entity<CalendarDay>();
        day.ToTable("calendar_days");
        day.HasKey(d => new { d.Year, d.Date }).HasName(DaysKey);
        day.Property(d => d.Year).HasColumnName("year");
        day.Property(d => d.Date).HasColumnName("date");
        MapKind(day.Property(d => d.Kind));

        var construction = builder.Entity<ConstructionCalendar>();
        construction.ToTable("construction_calendars");
        construction.HasKey(c => c.ConstructionId).HasName(ConstructionKey);
        construction.Property(c => c.ConstructionId).HasColumnName("construction_id").ValueGeneratedNever();
        construction.Property(c => c.WorkingWeek).HasColumnName("working_week");
        construction.Property(c => c.Shifts).HasColumnName("shifts");
        construction.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();
        construction.HasMany(c => c.Days).WithOne().HasForeignKey(d => d.ConstructionId).OnDelete(DeleteBehavior.Cascade);

        var constructionDay = builder.Entity<ConstructionCalendarDay>();
        constructionDay.ToTable("construction_calendar_days");
        constructionDay.HasKey(d => new { d.ConstructionId, d.Date }).HasName(ConstructionDaysKey);
        constructionDay.Property(d => d.ConstructionId).HasColumnName("construction_id");
        constructionDay.Property(d => d.Date).HasColumnName("date");
        MapKind(constructionDay.Property(d => d.Kind));
    }

    private static void MapKind(PropertyBuilder<CalendarDayKind> kind) =>
        kind.HasColumnName("kind").HasConversion<string>().HasMaxLength(7);
}
