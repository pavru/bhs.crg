using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PeriodClosureEventOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EventOrder",
                table: "period_closures",
                type: "bigint",
                nullable: true);

            // Старые записи не несут порядка фиксации. Восстанавливаем его по времени, а отмену
            // ставим после своего закрытия даже при равном или более раннем времени отмены.
            migrationBuilder.Sql("""
                WITH ordered AS (
                    SELECT event."Id",
                           row_number() OVER (
                               ORDER BY greatest(event."At", closed."At"), event."Kind" = 'Reopen',
                                        event."At", event."Id"
                           ) AS number
                    FROM period_closures AS event
                    LEFT JOIN period_closures AS closed ON closed."Id" = event."CancelsId"
                )
                UPDATE period_closures AS event
                SET "EventOrder" = ordered.number
                FROM ordered
                WHERE event."Id" = ordered."Id";
                """);

            migrationBuilder.AlterColumn<long>(
                name: "EventOrder",
                table: "period_closures",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_period_closures_EventOrder",
                table: "period_closures",
                column: "EventOrder",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_period_closures_EventOrder",
                table: "period_closures");

            migrationBuilder.DropColumn(
                name: "EventOrder",
                table: "period_closures");
        }
    }
}
