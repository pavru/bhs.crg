using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDataSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "dataset_files",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Format = table.Column<int>(type: "integer", nullable: false),
                    BlobPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dataset_files", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "dataset_sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SheetOrPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CachedSchema = table.Column<string>(type: "jsonb", nullable: false),
                    CachedRowCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dataset_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dataset_sources_dataset_files_FileId",
                        column: x => x.FileId,
                        principalTable: "dataset_files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "dataset_bindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetFieldKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Mapping = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dataset_bindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_dataset_bindings_dataset_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "dataset_sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dataset_bindings_InstanceId",
                table: "dataset_bindings",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_dataset_bindings_SourceId",
                table: "dataset_bindings",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_dataset_files_Scope_ScopeId",
                table: "dataset_files",
                columns: new[] { "Scope", "ScopeId" });

            migrationBuilder.CreateIndex(
                name: "IX_dataset_sources_FileId",
                table: "dataset_sources",
                column: "FileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dataset_bindings");

            migrationBuilder.DropTable(
                name: "dataset_sources");

            migrationBuilder.DropTable(
                name: "dataset_files");
        }
    }
}
