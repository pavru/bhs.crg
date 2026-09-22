using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <summary>
    /// Уровень правки схемы у типа (issue #956, ТЗ CORE-19).
    ///
    /// Все существующие типы становятся ОТКРЫТЫМИ, и это не умолчание, а состояние дел: их схемы
    /// заводил и правил администратор, полей модуля в них нет ни одного, и запирать в них нечего.
    /// Уровень объявляет модуль для СВОИХ типов — а модулей, заводящих типы, пока нет.
    /// </summary>
    public partial class SchemaEditLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EditLevel",
                table: "document_types",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""update document_types set "EditLevel" = 'Open';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EditLevel",
                table: "document_types");
        }
    }
}
