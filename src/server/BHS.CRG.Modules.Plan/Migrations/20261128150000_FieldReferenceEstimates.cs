using BHS.CRG.Modules.Plan.Data;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BHS.CRG.Modules.Plan.Migrations;

[DbContext(typeof(PlanDbContext))]
[Migration("20261128150000_FieldReferenceEstimates")]
public sealed class FieldReferenceEstimates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        FieldReferenceRevisionMigration.Add(migrationBuilder, "plan", "estimates", "estimate_rows", "estimate_row_materials");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        FieldReferenceRevisionMigration.Remove(migrationBuilder, "plan", "estimates", "estimate_rows", "estimate_row_materials");
}
