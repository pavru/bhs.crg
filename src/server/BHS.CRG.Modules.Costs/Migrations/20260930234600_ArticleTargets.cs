using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class ArticleTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "construction_id",
                schema: "costs",
                table: "invoice_allocations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "article_id",
                schema: "costs",
                table: "invoice_allocations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoice_allocations_one_target",
                schema: "costs",
                table: "invoice_allocations",
                sql: "(construction_id IS NULL) <> (article_id IS NULL) AND (section_id IS NULL OR construction_id IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_invoice_allocations_one_target",
                schema: "costs",
                table: "invoice_allocations");

            // Части на статьи вне строек прежней схеме выразить нечем: стройка в ней обязательна. Откат без
            // удаления падал бы на NOT NULL; с удалением — счёт с такой частью станет неразнесённым, и это
            // видно, а не выдумано.
            migrationBuilder.Sql("DELETE FROM costs.invoice_allocations WHERE article_id IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "article_id",
                schema: "costs",
                table: "invoice_allocations");

            migrationBuilder.AlterColumn<Guid>(
                name: "construction_id",
                schema: "costs",
                table: "invoice_allocations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
