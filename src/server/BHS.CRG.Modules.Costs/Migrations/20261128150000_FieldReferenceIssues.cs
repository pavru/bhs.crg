using BHS.CRG.Modules.Costs.Data;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BHS.CRG.Modules.Costs.Migrations;

[DbContext(typeof(CostsDbContext))]
[Migration("20261128150000_FieldReferenceIssues")]
public sealed class FieldReferenceIssues : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        FieldReferenceRevisionMigration.Add(migrationBuilder, "costs", "waybills", "waybill_lines");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        FieldReferenceRevisionMigration.Remove(migrationBuilder, "costs", "waybills", "waybill_lines");
}
