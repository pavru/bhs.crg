using BHS.CRG.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Plan.Data;

public sealed class PriceListEntry
{
    internal const string DateKey = "ux_price_list_entries_work_type_date";
    internal const decimal MaxPrice = 9_999_999_999_999_999.99m;

    private PriceListEntry() { }

    public Guid Id { get; private set; }
    public Guid WorkTypeId { get; private set; }
    public Guid UnitId { get; private set; }
    public string Unit { get; private set; } = string.Empty;
    public DateOnly ValidFrom { get; private set; }
    public decimal Price { get; private set; }
    public Guid? ChangedBy { get; private set; }
    public DateTimeOffset ChangedAt { get; private set; }

    internal static PriceListEntry Create(Guid workTypeId, Guid unitId, string unit, DateOnly validFrom) =>
        new()
        {
            Id = Guid.NewGuid(),
            WorkTypeId = workTypeId,
            UnitId = unitId,
            Unit = PlanText.Clip(unit, EstimateRow.UnitLength)!,
            ValidFrom = validFrom,
        };

    internal void SetPrice(decimal price, Guid? author, DateTimeOffset now)
    {
        if (price < 0 || price > MaxPrice || decimal.Round(price, 2) != price)
            throw new InvalidRequestException("Цена должна быть неотрицательной, до 16 цифр до запятой и до двух после.");
        Price = price;
        ChangedBy = author;
        ChangedAt = now;
    }

    internal static void Map(ModelBuilder builder)
    {
        var entry = builder.Entity<PriceListEntry>();
        entry.ToTable("price_list_entries");
        entry.HasKey(e => e.Id);
        entry.Property(e => e.Id).HasColumnName("id");
        entry.Property(e => e.WorkTypeId).HasColumnName("work_type_id");
        entry.Property(e => e.UnitId).HasColumnName("unit_id");
        entry.Property(e => e.Unit).HasColumnName("unit").HasMaxLength(EstimateRow.UnitLength);
        entry.Property(e => e.ValidFrom).HasColumnName("valid_from");
        entry.Property(e => e.Price).HasColumnName("price").HasPrecision(18, 2);
        entry.Property(e => e.ChangedBy).HasColumnName("changed_by");
        entry.Property(e => e.ChangedAt).HasColumnName("changed_at");
        entry.Property<uint>(PlanDbContext.RowVersion).IsRowVersion();
        entry.HasIndex(e => new { e.WorkTypeId, e.ValidFrom }).IsUnique().HasDatabaseName(DateKey);
        entry.HasIndex(e => e.UnitId).HasDatabaseName("ix_price_list_entries_unit_id");
    }
}
