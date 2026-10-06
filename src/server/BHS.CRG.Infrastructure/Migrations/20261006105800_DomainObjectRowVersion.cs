using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DomainObjectRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ⚠️ В базе эта миграция НЕ МЕНЯЕТ НИЧЕГО (issue #1214). «xmin» — системная колонка
            // PostgreSQL, она есть у каждой таблицы, и поставщик EF операции над системными колонками
            // пропускает: в SQL остаётся одна строка в истории миграций. Нужна миграция затем, чтобы
            // снимок модели знал о свойстве-версии объекта (тот же приём — у счёта, issue #1173).
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "domain_objects",
                type: "xid",
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                table: "domain_objects");
        }
    }
}
