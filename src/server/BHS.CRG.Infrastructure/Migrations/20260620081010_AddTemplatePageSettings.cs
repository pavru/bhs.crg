using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplatePageSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "templates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MarginBottom",
                table: "templates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MarginLeft",
                table: "templates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MarginRight",
                table: "templates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MarginTop",
                table: "templates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PageOrientation",
                table: "templates",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PageSize",
                table: "templates",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_templates_DocumentTypeId_IsDefault",
                table: "templates",
                columns: new[] { "DocumentTypeId", "IsDefault" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_templates_DocumentTypeId_IsDefault",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "MarginBottom",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "MarginLeft",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "MarginRight",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "MarginTop",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "PageOrientation",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "PageSize",
                table: "templates");
        }
    }
}
