using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSetSourceRecognitionStale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RecognitionStale",
                table: "dataset_sources",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecognitionStale",
                table: "dataset_sources");
        }
    }
}
