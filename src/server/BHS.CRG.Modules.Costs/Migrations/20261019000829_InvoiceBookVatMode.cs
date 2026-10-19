using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceBookVatMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "vat_mode",
                schema: "costs",
                table: "invoice_book_entries",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vat_mode_was",
                schema: "costs",
                table: "invoice_book_entries",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoice_book_entries_vat_mode",
                schema: "costs",
                table: "invoice_book_entries",
                sql: "(vat_mode_was IS NULL) = (vat_mode IS NULL) AND (vat_mode IS NULL OR vat_mode <> vat_mode_was)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_invoice_book_entries_vat_mode",
                schema: "costs",
                table: "invoice_book_entries");

            migrationBuilder.DropColumn(
                name: "vat_mode",
                schema: "costs",
                table: "invoice_book_entries");

            migrationBuilder.DropColumn(
                name: "vat_mode_was",
                schema: "costs",
                table: "invoice_book_entries");
        }
    }
}
