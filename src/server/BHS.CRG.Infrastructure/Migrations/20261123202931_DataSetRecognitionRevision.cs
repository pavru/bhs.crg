using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DataSetRecognitionRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RecognitionRevision",
                table: "dataset_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE dataset_files SET "RecognitionRevision" = gen_random_uuid()
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "RecognitionRevision",
                table: "dataset_files",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecognitionRevision",
                table: "dataset_files");
        }
    }
}
