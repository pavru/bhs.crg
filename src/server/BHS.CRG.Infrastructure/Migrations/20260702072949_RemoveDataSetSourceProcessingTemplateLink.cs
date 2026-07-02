using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDataSetSourceProcessingTemplateLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_dataset_sources_dataset_processing_templates_ProcessingTemp~",
                table: "dataset_sources");

            migrationBuilder.DropIndex(
                name: "IX_dataset_sources_ProcessingTemplateId",
                table: "dataset_sources");

            migrationBuilder.DropColumn(
                name: "ProcessingTemplateId",
                table: "dataset_sources");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProcessingTemplateId",
                table: "dataset_sources",
                type: "uuid",
                nullable: true);

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
    }
}
