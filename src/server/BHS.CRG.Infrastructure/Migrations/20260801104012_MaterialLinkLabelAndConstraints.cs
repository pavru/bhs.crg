using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MaterialLinkLabelAndConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Подготовка данных ПЕРЕД ограничениями (issue #554). Приложение мигрирует при старте,
            // поэтому одна дублирующая пара или одна висячая связка на чужой базе означала бы не
            // «миграция не прошла», а «API не поднялся»: CREATE UNIQUE INDEX даст 23505, AddForeignKey — 23503.
            //
            // Риск не гипотетический: миграция 20260715113426_GeneralizeIdentityTag переписывала
            // MaterialKey (срезала хвостовые точки и пробелы, «шт.» → «шт») — такая правка способна
            // схлопнуть две прежде различные строки в дубль. На нашей базе обе команды удаляют 0 строк.

            // Дубли: оставляем самую свежую связку на (Scope, ScopeId, MaterialKey). NULL-ы в ScopeId
            // сравниваем через IS NOT DISTINCT FROM — именно так их будет видеть новый индекс.
            migrationBuilder.Sql(
                """
                DELETE FROM material_quality_links a
                USING material_quality_links b
                WHERE a."Scope" = b."Scope"
                  AND a."ScopeId" IS NOT DISTINCT FROM b."ScopeId"
                  AND a."MaterialKey" = b."MaterialKey"
                  AND (a."UpdatedAt", a."Id") < (b."UpdatedAt", b."Id");
                """);

            // Висячие связки: документа, на который они ссылаются, больше нет.
            migrationBuilder.Sql(
                """
                DELETE FROM material_quality_links l
                WHERE NOT EXISTS (SELECT 1 FROM quality_documents q WHERE q."Id" = l."QualityDocumentId");
                """);

            migrationBuilder.DropIndex(
                name: "IX_material_quality_links_Scope_ScopeId_MaterialKey",
                table: "material_quality_links");

            migrationBuilder.AddColumn<string>(
                name: "MaterialLabel",
                table: "material_quality_links",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_material_quality_links_Scope_ScopeId_MaterialKey",
                table: "material_quality_links",
                columns: new[] { "Scope", "ScopeId", "MaterialKey" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.AddForeignKey(
                name: "FK_material_quality_links_quality_documents_QualityDocumentId",
                table: "material_quality_links",
                column: "QualityDocumentId",
                principalTable: "quality_documents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_material_quality_links_quality_documents_QualityDocumentId",
                table: "material_quality_links");

            migrationBuilder.DropIndex(
                name: "IX_material_quality_links_Scope_ScopeId_MaterialKey",
                table: "material_quality_links");

            migrationBuilder.DropColumn(
                name: "MaterialLabel",
                table: "material_quality_links");

            migrationBuilder.CreateIndex(
                name: "IX_material_quality_links_Scope_ScopeId_MaterialKey",
                table: "material_quality_links",
                columns: new[] { "Scope", "ScopeId", "MaterialKey" });
        }
    }
}
