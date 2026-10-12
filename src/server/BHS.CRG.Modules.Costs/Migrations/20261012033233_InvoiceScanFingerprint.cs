using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceScanFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "scan_fingerprint",
                schema: "costs",
                table: "invoices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_scan_fingerprint",
                schema: "costs",
                table: "invoices",
                column: "scan_fingerprint",
                filter: "scan_fingerprint IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_scan_fingerprint",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "scan_fingerprint",
                schema: "costs",
                table: "invoices");
        }
    }
}
