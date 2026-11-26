using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Modules.Costs.Data;

internal static class PurchaseRequestModel
{
    public static void Configure(ModelBuilder builder)
    {
        var request = builder.Entity<PurchaseRequest>();
        request.ToTable("purchase_requests");
        request.HasKey(r => r.Id);
        request.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();
        request.Property(r => r.Number).HasColumnName("number").UseIdentityByDefaultColumn();
        request.Property(r => r.ConstructionId).HasColumnName("construction_id");
        request.Property(r => r.CreatedBy).HasColumnName("created_by");
        request.Property(r => r.CreatedAt).HasColumnName("created_at");
        request.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        request.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        request.Property<uint>(CostsDbContext.RowVersion).IsRowVersion();
        request.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.RequestId).OnDelete(DeleteBehavior.Cascade);
        request.HasIndex(r => r.Number).IsUnique().HasDatabaseName("ux_purchase_requests_number");
        request.HasIndex(r => new { r.ConstructionId, r.Number }).HasDatabaseName("ix_purchase_requests_site_number");

        var line = builder.Entity<PurchaseRequestLine>();
        line.ToTable("purchase_request_lines", table =>
            table.HasCheckConstraint("ck_purchase_request_lines_quantity", "quantity >= 0"));
        line.HasKey(l => l.Id);
        line.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();
        line.Property(l => l.RequestId).HasColumnName("request_id");
        line.Property(l => l.Position).HasColumnName("position");
        line.Property(l => l.NomenclatureId).HasColumnName("nomenclature_id");
        line.Property(l => l.PlanItemId).HasColumnName("plan_item_id");
        line.Property(l => l.Quantity).HasColumnName("quantity").HasPrecision(20, PurchaseRequestLine.QuantityScale);
        line.Property(l => l.Unit).HasColumnName("unit").HasMaxLength(PurchaseRequestLine.UnitLength);
        line.Property(l => l.Note).HasColumnName("note").HasMaxLength(PurchaseRequestLine.NoteLength);
        line.HasIndex(l => new { l.RequestId, l.Position }).HasDatabaseName("ix_purchase_request_lines_order");
        line.HasIndex(l => l.NomenclatureId).HasDatabaseName("ix_purchase_request_lines_nomenclature");
        line.HasIndex(l => l.PlanItemId).HasDatabaseName("ix_purchase_request_lines_plan_item");
    }
}
