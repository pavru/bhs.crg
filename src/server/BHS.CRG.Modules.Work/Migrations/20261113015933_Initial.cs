using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Work.Migrations
{
    /// <summary>
    /// Первая миграция модуля «Учёт работ» (change work-10-skeleton): создаёт СХЕМУ. Таблиц в ней пока
    /// нет — первые приезжают вместе с адресами, которые их читают (назначения — work-11, отчёты —
    /// work-13), как было у `costs` и `plan` в их каркасах.
    ///
    /// Поэтому здесь нет ни одного CreateTable, и это не забытая работа: схема и история миграций
    /// модуля в ней — то, что включается на чистой и на рабочей базе и что переживает выключение
    /// модуля.
    ///
    /// Down схему НЕ удаляет: откат первой миграции не повод сносить чужие данные — в схеме к этому
    /// моменту могут стоять таблицы более поздних миграций, а у EnsureSchema обратного действия нет.
    /// </summary>
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "work");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
