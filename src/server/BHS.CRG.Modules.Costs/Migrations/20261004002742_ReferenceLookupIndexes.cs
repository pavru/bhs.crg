using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class ReferenceLookupIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_invoices_payer",
                schema: "costs",
                table: "invoices",
                column: "payer_id",
                filter: "payer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_nomenclature",
                schema: "costs",
                table: "invoice_lines",
                column: "nomenclature_id",
                filter: "nomenclature_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_allocations_article",
                schema: "costs",
                table: "invoice_allocations",
                column: "article_id",
                filter: "article_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_allocations_construction",
                schema: "costs",
                table: "invoice_allocations",
                column: "construction_id",
                filter: "construction_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_allocations_section",
                schema: "costs",
                table: "invoice_allocations",
                column: "section_id",
                filter: "section_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_invoices_payer",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "ix_invoice_lines_nomenclature",
                schema: "costs",
                table: "invoice_lines");

            migrationBuilder.DropIndex(
                name: "ix_invoice_allocations_article",
                schema: "costs",
                table: "invoice_allocations");

            migrationBuilder.DropIndex(
                name: "ix_invoice_allocations_construction",
                schema: "costs",
                table: "invoice_allocations");

            migrationBuilder.DropIndex(
                name: "ix_invoice_allocations_section",
                schema: "costs",
                table: "invoice_allocations");
        }
    }
}
