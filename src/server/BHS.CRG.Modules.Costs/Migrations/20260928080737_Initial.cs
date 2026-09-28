using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <summary>
    /// Первая миграция модуля (задача A2a этапа 2, issue #1072): создаёт СХЕМУ. Таблиц в ней пока нет —
    /// первая (счёт) приезжает задачей C1 (issue #1076) вместе с типом, формой и адресами, которые её
    /// читают.
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
            migrationBuilder.EnsureSchema(name: "costs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
