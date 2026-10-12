using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChoiceSearchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            // Функцию не знает модель — её заводит эта миграция (issue #1247). Обёртка над
            // array_to_string: та помечена STABLE, а генерируемой колонке нужна IMMUTABLE. Тело в
            // стандартной форме разбирается при создании и от search_path вызывающего не зависит.
            migrationBuilder.Sql("""
                CREATE FUNCTION choice_search_text(name text, aliases text[]) RETURNS text
                LANGUAGE sql IMMUTABLE PARALLEL SAFE
                RETURN coalesce(name, '') || E'\n' || array_to_string(aliases, E'\n');
                """);

            // ⚠️ Хранимая колонка переписывает таблицу под исключительной блокировкой. Замер: 300 000
            // строк — около секунды, ещё столько же строится индекс.
            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                table: "domain_objects",
                type: "text",
                nullable: true,
                computedColumnSql: "choice_search_text(\"DisplayName\", \"Aliases\")",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_domain_objects_CompositeTypeId_Archived",
                table: "domain_objects",
                column: "CompositeTypeId",
                filter: "\"ArchivedAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_domain_objects_SearchText",
                table: "domain_objects",
                column: "SearchText")
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_domain_objects_CompositeTypeId_Archived",
                table: "domain_objects");

            migrationBuilder.DropIndex(
                name: "IX_domain_objects_SearchText",
                table: "domain_objects");

            migrationBuilder.DropColumn(
                name: "SearchText",
                table: "domain_objects");

            migrationBuilder.Sql("DROP FUNCTION choice_search_text(text, text[]);");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
