using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class EstimateImportCommit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "committed_at",
                schema: "plan",
                table: "estimate_imports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "parsed_positions",
                schema: "plan",
                table: "estimate_imports",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "positions_in_file",
                schema: "plan",
                table: "estimate_imports",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_estimate_revisions_import_key",
                schema: "plan",
                table: "estimate_revisions",
                column: "import_key",
                unique: true,
                filter: "import_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_estimate_revisions_import_key",
                schema: "plan",
                table: "estimate_revisions");

            migrationBuilder.DropColumn(
                name: "committed_at",
                schema: "plan",
                table: "estimate_imports");

            migrationBuilder.DropColumn(
                name: "parsed_positions",
                schema: "plan",
                table: "estimate_imports");

            migrationBuilder.DropColumn(
                name: "positions_in_file",
                schema: "plan",
                table: "estimate_imports");
        }
    }
}
