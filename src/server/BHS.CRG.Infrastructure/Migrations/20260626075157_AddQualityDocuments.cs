using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddQualityDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "material_quality_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    MaterialKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    QualityDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_material_quality_links", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "quality_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Requisites = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    ScanBlobPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ScanFileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ScanMimeType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quality_documents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_material_quality_links_QualityDocumentId",
                table: "material_quality_links",
                column: "QualityDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_material_quality_links_Scope_ScopeId_MaterialKey",
                table: "material_quality_links",
                columns: new[] { "Scope", "ScopeId", "MaterialKey" });

            migrationBuilder.CreateIndex(
                name: "IX_quality_documents_DocumentTypeId",
                table: "quality_documents",
                column: "DocumentTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_quality_documents_Scope_ScopeId",
                table: "quality_documents",
                columns: new[] { "Scope", "ScopeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "material_quality_links");

            migrationBuilder.DropTable(
                name: "quality_documents");
        }
    }
}
