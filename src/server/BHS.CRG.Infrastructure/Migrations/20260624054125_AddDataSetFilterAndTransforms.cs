using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSetFilterAndTransforms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
        }
    }
}
