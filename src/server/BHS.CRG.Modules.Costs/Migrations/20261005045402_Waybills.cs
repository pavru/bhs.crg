using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class Waybills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "waybills",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: true),
                    warehouse = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    construction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    posted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    posted_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_waybills", x => x.id);
                    table.CheckConstraint("ck_waybills_posted", "state <> 'Posted' OR (issued_on IS NOT NULL AND construction_id IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "waybill_lines",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    waybill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    nomenclature_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_text = table.Column<string>(type: "text", nullable: true),
                    unit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_waybill_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_waybill_lines_waybills_waybill_id",
                        column: x => x.waybill_id,
                        principalSchema: "costs",
                        principalTable: "waybills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_waybill_lines_nomenclature",
                schema: "costs",
                table: "waybill_lines",
                column: "nomenclature_id",
                filter: "nomenclature_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_waybill_lines_order",
                schema: "costs",
                table: "waybill_lines",
                columns: new[] { "waybill_id", "ordinal" });

            migrationBuilder.CreateIndex(
                name: "ix_waybill_lines_unmatched",
                schema: "costs",
                table: "waybill_lines",
                column: "waybill_id",
                filter: "nomenclature_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_waybills_issued",
                schema: "costs",
                table: "waybills",
                columns: new[] { "construction_id", "issued_on" },
                filter: "state = 'Posted'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "waybill_lines",
                schema: "costs");

            migrationBuilder.DropTable(
                name: "waybills",
                schema: "costs");
        }
    }
}
