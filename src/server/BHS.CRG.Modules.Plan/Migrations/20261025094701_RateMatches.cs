using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class RateMatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "rate_match_id",
                schema: "plan",
                table: "estimate_rows",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "matched_at",
                schema: "plan",
                table: "estimate_revisions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "normative_base",
                schema: "plan",
                table: "estimate_revisions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "rate_matches",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    rate_code = table.Column<string>(type: "text", nullable: false),
                    normative_base = table.Column<string>(type: "text", nullable: true),
                    work_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    factor = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    source_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rate_matches", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_rate_matches_unit_id",
                schema: "plan",
                table: "rate_matches",
                column: "unit_id");

            migrationBuilder.CreateIndex(
                name: "ix_rate_matches_work_type_id",
                schema: "plan",
                table: "rate_matches",
                column: "work_type_id");

            migrationBuilder.CreateIndex(
                name: "ux_rate_matches_key",
                schema: "plan",
                table: "rate_matches",
                column: "key_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rate_matches",
                schema: "plan");

            migrationBuilder.DropColumn(
                name: "rate_match_id",
                schema: "plan",
                table: "estimate_rows");

            migrationBuilder.DropColumn(
                name: "matched_at",
                schema: "plan",
                table: "estimate_revisions");

            migrationBuilder.DropColumn(
                name: "normative_base",
                schema: "plan",
                table: "estimate_revisions");
        }
    }
}
