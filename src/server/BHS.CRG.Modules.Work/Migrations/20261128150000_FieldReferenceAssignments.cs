using BHS.CRG.Modules.Work.Data;
using BHS.CRG.Modules.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BHS.CRG.Modules.Work.Migrations;

[DbContext(typeof(WorkDbContext))]
[Migration("20261128150000_FieldReferenceAssignments")]
public sealed class FieldReferenceAssignments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        FieldReferenceRevisionMigration.Add(migrationBuilder, "work", "assignments");

    protected override void Down(MigrationBuilder migrationBuilder) =>
        FieldReferenceRevisionMigration.Remove(migrationBuilder, "work", "assignments");
}
