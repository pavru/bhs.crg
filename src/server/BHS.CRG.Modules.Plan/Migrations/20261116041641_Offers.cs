using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class Offers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "offers",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    construction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    current_version = table.Column<int>(type: "integer", nullable: false),
                    vat_mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    vat_rate = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                    markup = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    lines_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    vat_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offers", x => x.id);
                    table.ForeignKey(
                        name: "fk_offers_revision",
                        column: x => x.revision_id,
                        principalSchema: "plan",
                        principalTable: "estimate_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "offer_lines",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    revision_row_id = table.Column<Guid>(type: "uuid", nullable: true),
                    work_type_id = table.Column<Guid>(type: "uuid", nullable: true),
                    material = table.Column<bool>(type: "boolean", nullable: false),
                    lsr_section_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    lsr_section_name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    volume = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    unit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    markup = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                    price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_offer_lines_offer",
                        column: x => x.offer_id,
                        principalSchema: "plan",
                        principalTable: "offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_offer_lines_revision_row",
                        column: x => x.revision_row_id,
                        principalSchema: "plan",
                        principalTable: "estimate_rows",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "offer_section_markups",
                schema: "plan",
                columns: table => new
                {
                    offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    section_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    markup = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_offer_section_markups", x => new { x.offer_id, x.section_code });
                    table.ForeignKey(
                        name: "fk_offer_section_markups_offer",
                        column: x => x.offer_id,
                        principalSchema: "plan",
                        principalTable: "offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_offer_lines_offer_order",
                schema: "plan",
                table: "offer_lines",
                columns: new[] { "offer_id", "order" });

            migrationBuilder.CreateIndex(
                name: "ix_offer_lines_revision_row_id",
                schema: "plan",
                table: "offer_lines",
                column: "revision_row_id");

            migrationBuilder.CreateIndex(
                name: "ix_offer_lines_work_type_id",
                schema: "plan",
                table: "offer_lines",
                column: "work_type_id",
                filter: "work_type_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_offers_construction_id",
                schema: "plan",
                table: "offers",
                column: "construction_id");

            migrationBuilder.CreateIndex(
                name: "ix_offers_customer_id",
                schema: "plan",
                table: "offers",
                column: "customer_id",
                filter: "customer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_offers_document_type_id",
                schema: "plan",
                table: "offers",
                column: "document_type_id");

            migrationBuilder.CreateIndex(
                name: "ix_offers_revision_id",
                schema: "plan",
                table: "offers",
                column: "revision_id");

            migrationBuilder.CreateIndex(
                name: "ux_offers_number",
                schema: "plan",
                table: "offers",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_offers_year_sequence",
                schema: "plan",
                table: "offers",
                columns: new[] { "year", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "offer_lines",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "offer_section_markups",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "offers",
                schema: "plan");
        }
    }
}
