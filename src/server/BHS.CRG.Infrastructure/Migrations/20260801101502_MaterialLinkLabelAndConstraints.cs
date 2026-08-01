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
                unique: true);

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
