using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class Conjuncture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conjuncture_analyses",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    current_price_date = table.Column<DateOnly>(type: "date", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    estimate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_key = table.Column<Guid>(type: "uuid", nullable: false),
                    blob_path = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    program_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    schema_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    name = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    creation_date = table.Column<DateOnly>(type: "date", nullable: true),
                    parsed_positions = table.Column<int>(type: "integer", nullable: false),
                    positions_in_file = table.Column<int>(type: "integer", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    imported_by_name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conjuncture_analyses", x => x.id);
                    table.ForeignKey(
                        name: "FK_conjuncture_analyses_estimates_estimate_id",
                        column: x => x.estimate_id,
                        principalSchema: "plan",
                        principalTable: "estimates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "volume_statements",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    estimate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_key = table.Column<Guid>(type: "uuid", nullable: false),
                    blob_path = table.Column<string>(type: "text", nullable: false),
                    file_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    program_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    schema_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    name = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    creation_date = table.Column<DateOnly>(type: "date", nullable: true),
                    parsed_positions = table.Column<int>(type: "integer", nullable: false),
                    positions_in_file = table.Column<int>(type: "integer", nullable: false),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    imported_by = table.Column<Guid>(type: "uuid", nullable: true),
                    imported_by_name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volume_statements", x => x.id);
                    table.ForeignKey(
                        name: "FK_volume_statements_estimates_estimate_id",
                        column: x => x.estimate_id,
                        principalSchema: "plan",
                        principalTable: "estimates",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conjuncture_items",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    analysis_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    chapter_index = table.Column<int>(type: "integer", nullable: false),
                    chapter_caption = table.Column<string>(type: "text", nullable: false),
                    number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    caption = table.Column<string>(type: "text", nullable: false),
                    units = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_inactive = table.Column<bool>(type: "boolean", nullable: false),
                    selection = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    selected_offer = table.Column<int>(type: "integer", nullable: true),
                    selected_price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conjuncture_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_conjuncture_items_conjuncture_analyses_analysis_id",
                        column: x => x.analysis_id,
                        principalSchema: "plan",
                        principalTable: "conjuncture_analyses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "volume_statement_lines",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    statement_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    chapter_index = table.Column<int>(type: "integer", nullable: false),
                    chapter_caption = table.Column<string>(type: "text", nullable: false),
                    number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    caption = table.Column<string>(type: "text", nullable: false),
                    code = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    units = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    formula = table.Column<string>(type: "text", nullable: true),
                    drawings = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    is_subordinate = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_volume_statement_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_volume_statement_lines_volume_statements_statement_id",
                        column: x => x.statement_id,
                        principalSchema: "plan",
                        principalTable: "volume_statements",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "conjuncture_offers",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    caption = table.Column<string>(type: "text", nullable: false),
                    units = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    resource_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    supplier_name = table.Column<string>(type: "text", nullable: true),
                    supplier_inn = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    vat = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    price_with_vat = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    offer_date = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conjuncture_offers", x => x.id);
                    table.ForeignKey(
                        name: "FK_conjuncture_offers_conjuncture_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "plan",
                        principalTable: "conjuncture_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_conjuncture_analyses_estimate_id",
                schema: "plan",
                table: "conjuncture_analyses",
                column: "estimate_id");

            migrationBuilder.CreateIndex(
                name: "ux_conjuncture_analyses_import_key",
                schema: "plan",
                table: "conjuncture_analyses",
                column: "import_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_conjuncture_items_analysis_order",
                schema: "plan",
                table: "conjuncture_items",
                columns: new[] { "analysis_id", "order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_conjuncture_offers_item_order",
                schema: "plan",
                table: "conjuncture_offers",
                columns: new[] { "item_id", "order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_volume_statement_lines_statement_order",
                schema: "plan",
                table: "volume_statement_lines",
                columns: new[] { "statement_id", "order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_volume_statements_estimate_id",
                schema: "plan",
                table: "volume_statements",
                column: "estimate_id");

            migrationBuilder.CreateIndex(
                name: "ux_volume_statements_import_key",
                schema: "plan",
                table: "volume_statements",
                column: "import_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conjuncture_offers",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "volume_statement_lines",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "conjuncture_items",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "volume_statements",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "conjuncture_analyses",
                schema: "plan");
        }
    }
}
