using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateParameters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Parameters",
                table: "templates",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TemplateParams",
                table: "document_instances",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Parameters",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "TemplateParams",
                table: "document_instances");
        }
    }
}
