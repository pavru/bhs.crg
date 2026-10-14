using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SavedTableViewTitleIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_table_views_own_title",
                table: "table_views");

            migrationBuilder.DropIndex(
                name: "IX_table_views_shared_title",
                table: "table_views");

            // Имя представления уникально БЕЗ учёта регистра (issue #1095, ревью PR #1323): «Счета» и
            // «счета» в одном списке человек не различит. Прежние индексы регистр различали, и
            // правило держала только проверка в службе — а она не атомарна: два одновременных
            // сохранения проходили обе. Индекс по выражению моделью не выразить, поэтому руками.
            // Таблица к этому месту пуста у всех: её завела предыдущая миграция того же выпуска.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_table_views_shared_title"
                    ON table_views ("TableAddress", lower("Title")) WHERE "Shared";
                CREATE UNIQUE INDEX "IX_table_views_own_title"
                    ON table_views ("TableAddress", "OwnerId", lower("Title")) WHERE NOT "Shared";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX "IX_table_views_shared_title";
                DROP INDEX "IX_table_views_own_title";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_table_views_own_title",
                table: "table_views",
                columns: new[] { "TableAddress", "OwnerId", "Title" },
                unique: true,
                filter: "NOT \"Shared\"");

            migrationBuilder.CreateIndex(
                name: "IX_table_views_shared_title",
                table: "table_views",
                columns: new[] { "TableAddress", "Title" },
                unique: true,
                filter: "\"Shared\"");
        }
    }
}
