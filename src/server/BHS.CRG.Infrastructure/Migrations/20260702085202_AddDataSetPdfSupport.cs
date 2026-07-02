using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSetPdfSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CachedData",
                table: "dataset_sources",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "dataset_sources",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CachedData",
                table: "dataset_sources");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "dataset_sources");
        }
    }
}
