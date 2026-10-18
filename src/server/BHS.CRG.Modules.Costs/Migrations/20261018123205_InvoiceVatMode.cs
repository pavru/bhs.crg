using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceVatMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "vat_mode",
                schema: "costs",
                table: "invoices",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Included");

            migrationBuilder.AddColumn<decimal>(
                name: "net_amount",
                schema: "costs",
                table: "invoice_lines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoice_lines_net_amount",
                schema: "costs",
                table: "invoice_lines",
                sql: "net_amount IS NULL OR ((amount IS NULL) = (vat_amount IS NULL) AND (amount IS NULL OR amount = net_amount + vat_amount))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_invoice_lines_net_amount",
                schema: "costs",
                table: "invoice_lines");

            migrationBuilder.DropColumn(
                name: "vat_mode",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "net_amount",
                schema: "costs",
                table: "invoice_lines");
        }
    }
}
