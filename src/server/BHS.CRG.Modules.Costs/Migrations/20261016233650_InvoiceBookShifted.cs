using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceBookShifted : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "shifted",
                schema: "costs",
                table: "invoice_book_lines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Строки, записанные до PR 6d, — догадкой (решение владельца 17.10.2026; разбор Архитектора).
            // Открытие несёт свои дни, оплата (у неё «до» пусто) почти всегда ложится своим днём — переноса
            // у них нет. У исправления, отмены оплаты и выравнивания строка ДНЯ ЗАПИСИ — перенос: свои дни
            // лежат в прошлом, а день записи не раньше сегодня. Ошибка догадки — своё зерно самого дня
            // записи: прежде своя и перенесённая строки одной ячейки на этот день складывались в одну.
            // Вид записи лежит именем (HasConversion<string>).
            migrationBuilder.Sql("""
                UPDATE costs.invoice_book_lines AS l
                SET shifted = true
                FROM costs.invoice_book_entries AS e
                WHERE l.entry_id = e.id
                  AND e.kind IN ('Correction', 'Unpayment', 'Alignment')
                  AND l.day = e.day;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "shifted",
                schema: "costs",
                table: "invoice_book_lines");
        }
    }
}
