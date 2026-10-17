using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceBookShifted : Migration
    {
        /// <summary>
        /// Строки, записанные до PR 6d, — догадкой (решение владельца 17.10.2026; разбор Архитектора).
        ///
        /// <para>Открытие несёт свои дни — переноса у него нет. У исправления, отмены оплаты и выравнивания
        /// строка ДНЯ ЗАПИСИ — перенос: свои дни лежат в прошлом, а день записи не раньше сегодня. Но «да» —
        /// только там, где уверены (ревью PR #1395): с тех пор признак участвует в проводке — перенесённая
        /// строка не делает пару «разошедшейся», — и ошибочное «да» уводило бы днём записи пару, которую
        /// прежнее правило не уводило. Ошибочное «нет» оставляет ровно прежнее поведение. Поэтому:</para>
        /// <list type="bullet">
        /// <item>оплата — «нет» всегда: она переносит лишь пару, которую однажды увели, и угадать это по строке
        /// нечем;</item>
        /// <item>строка дня записи — «нет», если раньше у счёта была строка открытия или оплаты тем же днём:
        /// своё зерно дня записи бывает, только когда у документа были деньги этим днём, а в книгу такие
        /// деньги попадают именно так.</item>
        /// </list>
        /// <para>Остаётся названная ошибка — склеенная строка: прежде своя и перенесённая строки одной ячейки
        /// на день записи складывались в одну, и разделить их нечем.</para>
        ///
        /// <para>Вид записи лежит именем (HasConversion&lt;string&gt;). Текст — полем, чтобы тест мог прогнать
        /// ту же догадку на живой базе.</para>
        /// </summary>
        public const string Guess = """
            UPDATE costs.invoice_book_lines AS l
            SET shifted = true
            FROM costs.invoice_book_entries AS e
            WHERE l.entry_id = e.id
              AND e.kind IN ('Correction', 'Unpayment', 'Alignment')
              AND l.day = e.day
              AND NOT EXISTS (
                SELECT 1
                FROM costs.invoice_book_lines AS l2
                JOIN costs.invoice_book_entries AS e2 ON l2.entry_id = e2.id
                WHERE e2.invoice_id = e.invoice_id
                  AND e2.seq < e.seq
                  AND e2.kind IN ('Opening', 'Payment')
                  AND l2.day = e.day);
            """;

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

            // Строки, записанные до PR 6d, — догадкой: почему так и где она ошибается — у Guess.
            migrationBuilder.Sql(Guess);
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
