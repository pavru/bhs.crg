using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ⚠️ В базе эта миграция НЕ МЕНЯЕТ НИЧЕГО (issue #1173). «xmin» — системная колонка
            // PostgreSQL, она есть у каждой таблицы, и поставщик EF операции над системными колонками
            // пропускает: в SQL остаётся одна строка в истории миграций. Нужна миграция затем, чтобы
            // снимок модели знал о свойстве-версии — иначе старт останавливается на «модель
            // расходится со снимком». Проверено: `dotnet ef migrations script` даёт только INSERT в
            // __EFMigrationsHistory.
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "costs",
                table: "invoices",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "costs",
                table: "invoices");
        }
    }
}
