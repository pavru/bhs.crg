using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSetProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComputedColumns",
                table: "dataset_bindings");

            migrationBuilder.DropColumn(
                name: "RowFilter",
                table: "dataset_bindings");

            migrationBuilder.DropColumn(
                name: "ComputedColumns",
                table: "dataset_binding_templates");

            migrationBuilder.DropColumn(
                name: "RowFilter",
                table: "dataset_binding_templates");

            migrationBuilder.AddColumn<string>(
                name: "ComputedColumns",
                table: "dataset_sources",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProcessingTemplateId",
                table: "dataset_sources",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RowFilter",
                table: "dataset_sources",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SortSpec",
                table: "dataset_sources",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "dataset_processing_templates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RowFilter = table.Column<string>(type: "jsonb", nullable: true),
                    ComputedColumns = table.Column<string>(type: "jsonb", nullable: true),
                    SortSpec = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dataset_processing_templates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dataset_sources_ProcessingTemplateId",
                table: "dataset_sources",
                column: "ProcessingTemplateId");

            migrationBuilder.AddForeignKey(
                name: "FK_dataset_sources_dataset_processing_templates_ProcessingTemp~",
                table: "dataset_sources",
                column: "ProcessingTemplateId",
                principalTable: "dataset_processing_templates",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_dataset_sources_dataset_processing_templates_ProcessingTemp~",
                table: "dataset_sources");

            migrationBuilder.DropTable(
                name: "dataset_processing_templates");

            migrationBuilder.DropIndex(
                name: "IX_dataset_sources_ProcessingTemplateId",
                table: "dataset_sources");

            migrationBuilder.DropColumn(
                name: "ComputedColumns",
                table: "dataset_sources");

            migrationBuilder.DropColumn(
                name: "ProcessingTemplateId",
                table: "dataset_sources");

            migrationBuilder.DropColumn(
                name: "RowFilter",
                table: "dataset_sources");

            migrationBuilder.DropColumn(
                name: "SortSpec",
                table: "dataset_sources");

            migrationBuilder.AddColumn<string>(
                name: "ComputedColumns",
                table: "dataset_bindings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RowFilter",
                table: "dataset_bindings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ComputedColumns",
                table: "dataset_binding_templates",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RowFilter",
                table: "dataset_binding_templates",
                type: "jsonb",
                nullable: true);
        }
    }
}
