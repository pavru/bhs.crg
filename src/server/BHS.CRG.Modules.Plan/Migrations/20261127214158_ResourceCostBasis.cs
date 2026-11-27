using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class ResourceCostBasis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "amount_basis",
                schema: "plan",
                table: "estimate_row_materials",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "unit_price_basis",
                schema: "plan",
                table: "estimate_row_materials",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Unknown");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "amount_basis",
                schema: "plan",
                table: "estimate_row_materials");

            migrationBuilder.DropColumn(
                name: "unit_price_basis",
                schema: "plan",
                table: "estimate_row_materials");
        }
    }
}
