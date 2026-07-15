using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeIdentityTag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Обобщение тэга material.identity → identity (issue #183). Тэг хранится как строковый
            // элемент массива fields[].tags внутри DocumentType.Schema (JSONB) — детерминированная
            // текстовая замена ровно квотированного значения.
            migrationBuilder.Sql(
                """UPDATE document_types SET "Schema" = replace("Schema"::text, '"material.identity"', '"identity"')::jsonb WHERE "Schema"::text LIKE '%"material.identity"%';""");

            // Приведение хранимых ключей связей материал↔документ качества к новой канонической
            // нормализации (MatchKeyNormalizer теперь срезает завершающие точки/пробелы). Без этого
            // ключ вида «…шт.» перестал бы сопоставляться сам с собой на генерации.
            migrationBuilder.Sql(
                """UPDATE material_quality_links SET "MaterialKey" = regexp_replace("MaterialKey", '[. ]+$', '') WHERE "MaterialKey" ~ '[. ]$';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Обратное переименование тэга. Срез хвостовых точек ключей необратим (данные уже
            // канонизированы) — и корректен для обеих версий нормализатора, откат не требуется.
            migrationBuilder.Sql(
                """UPDATE document_types SET "Schema" = replace("Schema"::text, '"identity"', '"material.identity"')::jsonb WHERE "Schema"::text LIKE '%"identity"%';""");
        }
    }
}
