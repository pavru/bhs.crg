using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class EstimateUiCarriers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Строки прежних редакций — работы (plan-31, Risks): перечитывать файлы не требуется, а пустой вид
            // EF не прочёл бы вовсе. Номер файла у них остаётся пустым — экран покажет номер в разделе.
            // Умолчание нужно только им и снимается сразу: в снимке модели его нет, и база с умолчанием, которого
            // EF не знает, разошлась бы со следующей миграцией этой колонки, а вставка мимо EF молча получала бы Work.
            migrationBuilder.AddColumn<string>(
                name: "kind",
                schema: "plan",
                table: "estimate_rows",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Work");
            migrationBuilder.Sql("ALTER TABLE plan.estimate_rows ALTER COLUMN kind DROP DEFAULT;");

            migrationBuilder.AddColumn<string>(
                name: "number",
                schema: "plan",
                table: "estimate_rows",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "subordinate",
                schema: "plan",
                table: "estimate_rows",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "skipped",
                schema: "plan",
                table: "estimate_imports",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kind",
                schema: "plan",
                table: "estimate_rows");

            migrationBuilder.DropColumn(
                name: "number",
                schema: "plan",
                table: "estimate_rows");

            migrationBuilder.DropColumn(
                name: "subordinate",
                schema: "plan",
                table: "estimate_rows");

            migrationBuilder.DropColumn(
                name: "skipped",
                schema: "plan",
                table: "estimate_imports");
        }
    }
}
