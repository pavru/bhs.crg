using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TemplateDocumentTypeFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Шаблоны, потерявшие свой тип документа (issue #833). Внешнего ключа не было, и такие
            // сироты копились годами: на рабочей базе их семь. В интерфейсе их не увидеть вовсе —
            // список шаблонов строится от типа, — а в резервную копию они попадали и отбрасывались
            // при восстановлении с предупреждением, то есть обнаруживались после аварии.
            //
            // Удаляем ЗДЕСЬ, потому что иначе внешний ключ не создастся. Потеря невелика и названа
            // прямо: шаблон без типа сгенерировать нечего, открыть его нельзя, отредактировать
            // тоже. Пути к ним из приложения нет и не было.
            migrationBuilder.Sql(
                """
                DELETE FROM templates t
                WHERE NOT EXISTS (SELECT 1 FROM document_types d WHERE d."Id" = t."DocumentTypeId");
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_templates_document_types_DocumentTypeId",
                table: "templates",
                column: "DocumentTypeId",
                principalTable: "document_types",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        /// <summary>
        /// Обратно снимается только ключ. Удалённые сироты не возвращаются — их и не из чего
        /// вернуть; откат миграции восстанавливает СХЕМУ, а не данные, и делать вид, что иначе,
        /// было бы хуже молчания.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_templates_document_types_DocumentTypeId",
                table: "templates");
        }
    }
}
