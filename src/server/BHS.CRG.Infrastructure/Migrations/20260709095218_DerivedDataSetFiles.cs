using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DerivedDataSetFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "dataset_files",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Uploaded");

            migrationBuilder.AddColumn<string>(
                name: "OriginKey",
                table: "dataset_files",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentFileId",
                table: "dataset_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecognizedData",
                table: "dataset_files",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecognizedSchema",
                table: "dataset_files",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_dataset_files_ParentFileId",
                table: "dataset_files",
                column: "ParentFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_dataset_files_ParentFileId",
                table: "dataset_files");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "dataset_files");

            migrationBuilder.DropColumn(
                name: "OriginKey",
                table: "dataset_files");

            migrationBuilder.DropColumn(
                name: "ParentFileId",
                table: "dataset_files");

            migrationBuilder.DropColumn(
                name: "RecognizedData",
                table: "dataset_files");

            migrationBuilder.DropColumn(
                name: "RecognizedSchema",
                table: "dataset_files");
        }
    }
}
