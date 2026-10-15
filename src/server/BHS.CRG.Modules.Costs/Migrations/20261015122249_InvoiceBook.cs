using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceBook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "book_seq",
                schema: "costs",
                table: "invoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "invoice_book_entries",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    tolerance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: true),
                    author_id = table.Column<Guid>(type: "uuid", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_book_entries", x => x.id);
                    table.ForeignKey(
                        name: "FK_invoice_book_entries_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "costs",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_book_lines",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    construction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    section_id = table.Column<Guid>(type: "uuid", nullable: true),
                    article_id = table.Column<Guid>(type: "uuid", nullable: true),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    gross = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    net = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    vat_unknown = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_book_lines", x => x.id);
                    table.CheckConstraint("ck_invoice_book_lines_one_target", "construction_id IS NULL OR article_id IS NULL");
                    table.ForeignKey(
                        name: "FK_invoice_book_lines_invoice_book_entries_entry_id",
                        column: x => x.entry_id,
                        principalSchema: "costs",
                        principalTable: "invoice_book_entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_invoice_book_entries_seq",
                schema: "costs",
                table: "invoice_book_entries",
                columns: new[] { "invoice_id", "seq" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_invoice_book_lines_order",
                schema: "costs",
                table: "invoice_book_lines",
                columns: new[] { "entry_id", "ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoice_book_lines",
                schema: "costs");

            migrationBuilder.DropTable(
                name: "invoice_book_entries",
                schema: "costs");

            migrationBuilder.DropColumn(
                name: "book_seq",
                schema: "costs",
                table: "invoices");
        }
    }
}
