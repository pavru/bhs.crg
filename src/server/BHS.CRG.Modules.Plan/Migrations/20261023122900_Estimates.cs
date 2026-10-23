using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class Estimates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "plan");

            migrationBuilder.CreateTable(
                name: "estimate_revisions",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    estimate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    source_blob_path = table.Column<string>(type: "text", nullable: true),
                    source_file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    schema_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    import_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    imported_by_name = table.Column<string>(type: "text", nullable: true),
                    frozen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    frozen_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    frozen_label = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_estimate_revisions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "estimate_rows",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    lsr_section_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    lsr_section_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    number_in_section = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    rate_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    source_volume = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    source_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    work_type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    factor = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    volume = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    plan_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    unit_cost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_estimate_rows", x => x.id);
                    table.ForeignKey(
                        name: "FK_estimate_rows_estimate_revisions_revision_id",
                        column: x => x.revision_id,
                        principalSchema: "plan",
                        principalTable: "estimate_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "estimates",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    construction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    current_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_estimates", x => x.id);
                    table.ForeignKey(
                        name: "fk_estimates_current_revision",
                        column: x => x.current_revision_id,
                        principalSchema: "plan",
                        principalTable: "estimate_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "estimate_row_labor",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    man_hours = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_estimate_row_labor", x => x.id);
                    table.ForeignKey(
                        name: "FK_estimate_row_labor_estimate_rows_row_id",
                        column: x => x.row_id,
                        principalSchema: "plan",
                        principalTable: "estimate_rows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "estimate_row_materials",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    nomenclature_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_name = table.Column<string>(type: "text", nullable: false),
                    source_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    source_unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_estimate_row_materials", x => x.id);
                    table.ForeignKey(
                        name: "FK_estimate_row_materials_estimate_rows_row_id",
                        column: x => x.row_id,
                        principalSchema: "plan",
                        principalTable: "estimate_rows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_estimate_revisions_estimate_number",
                schema: "plan",
                table: "estimate_revisions",
                columns: new[] { "estimate_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_estimate_row_labor_row_id",
                schema: "plan",
                table: "estimate_row_labor",
                column: "row_id");

            migrationBuilder.CreateIndex(
                name: "ix_estimate_row_materials_nomenclature_id",
                schema: "plan",
                table: "estimate_row_materials",
                column: "nomenclature_id",
                filter: "nomenclature_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_estimate_row_materials_row_id",
                schema: "plan",
                table: "estimate_row_materials",
                column: "row_id");

            migrationBuilder.CreateIndex(
                name: "ix_estimate_rows_plan_item_id",
                schema: "plan",
                table: "estimate_rows",
                column: "plan_item_id",
                filter: "plan_item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_estimate_rows_revision_order",
                schema: "plan",
                table: "estimate_rows",
                columns: new[] { "revision_id", "order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_estimate_rows_unit_id",
                schema: "plan",
                table: "estimate_rows",
                column: "unit_id",
                filter: "unit_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_estimate_rows_work_type_id",
                schema: "plan",
                table: "estimate_rows",
                column: "work_type_id",
                filter: "work_type_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_estimates_construction_id",
                schema: "plan",
                table: "estimates",
                column: "construction_id");

            migrationBuilder.CreateIndex(
                name: "ix_estimates_current_revision_id",
                schema: "plan",
                table: "estimates",
                column: "current_revision_id");

            migrationBuilder.CreateIndex(
                name: "ix_estimates_section_id",
                schema: "plan",
                table: "estimates",
                column: "section_id",
                filter: "section_id IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_estimate_revisions_estimate",
                schema: "plan",
                table: "estimate_revisions",
                column: "estimate_id",
                principalSchema: "plan",
                principalTable: "estimates",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_estimate_revisions_estimate",
                schema: "plan",
                table: "estimate_revisions");

            migrationBuilder.DropTable(
                name: "estimate_row_labor",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "estimate_row_materials",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "estimate_rows",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "estimates",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "estimate_revisions",
                schema: "plan");
        }
    }
}
