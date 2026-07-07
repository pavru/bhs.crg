using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTemplateGeneration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TemplateId",
                table: "generated_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateIds",
                table: "document_instances",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TemplateId",
                table: "generated_files");

            migrationBuilder.DropColumn(
                name: "TemplateIds",
                table: "document_instances");
        }
    }
}
