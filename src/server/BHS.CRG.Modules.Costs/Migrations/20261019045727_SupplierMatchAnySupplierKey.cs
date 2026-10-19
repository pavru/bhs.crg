using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class SupplierMatchAnySupplierKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_supplier_matches_kind_key",
                schema: "costs",
                table: "supplier_matches",
                columns: new[] { "kind", "key_hash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_supplier_matches_kind_key",
                schema: "costs",
                table: "supplier_matches");
        }
    }
}
