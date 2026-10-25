using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class ResourceMatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "resource_match_id",
                schema: "plan",
                table: "estimate_row_materials",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "resource_matches",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    key_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    source_text = table.Column<string>(type: "text", nullable: false),
                    nomenclature_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_resource_matches", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_resource_matches_nomenclature_id",
                schema: "plan",
                table: "resource_matches",
                column: "nomenclature_id");

            migrationBuilder.CreateIndex(
                name: "ux_resource_matches_key",
                schema: "plan",
                table: "resource_matches",
                columns: new[] { "kind", "key_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "resource_matches",
                schema: "plan");

            migrationBuilder.DropColumn(
                name: "resource_match_id",
                schema: "plan",
                table: "estimate_row_materials");
        }
    }
}
