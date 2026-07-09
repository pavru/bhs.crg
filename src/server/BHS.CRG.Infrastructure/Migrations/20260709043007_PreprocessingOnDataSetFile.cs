using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PreprocessingOnDataSetFile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Grouping",
                table: "dataset_files",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreprocessingProfile",
                table: "dataset_files",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RecognitionStale",
                table: "dataset_files",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Grouping",
                table: "dataset_files");

            migrationBuilder.DropColumn(
                name: "PreprocessingProfile",
                table: "dataset_files");

            migrationBuilder.DropColumn(
                name: "RecognitionStale",
                table: "dataset_files");
        }
    }
}
