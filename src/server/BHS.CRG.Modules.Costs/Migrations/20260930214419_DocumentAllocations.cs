using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class DocumentAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "line_id",
                schema: "costs",
                table: "invoice_allocations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoice_allocations_document_amount",
                schema: "costs",
                table: "invoice_allocations",
                sql: "line_id IS NOT NULL OR (quantity IS NULL AND amount IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_invoice_allocations_document_amount",
                schema: "costs",
                table: "invoice_allocations");

            // Частям счёта целиком (без строки) в прежней схеме места нет: строка у части обязательна.
            migrationBuilder.Sql("DELETE FROM costs.invoice_allocations WHERE line_id IS NULL");

            migrationBuilder.AlterColumn<Guid>(
                name: "line_id",
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
