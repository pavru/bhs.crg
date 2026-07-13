using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UnifyDomainObject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Цутовер объектов (issue #84): документы и записи общих данных пересоздаются заново
            // (решение — чистый разрыв, без переноса данных). Существующие сгенерированные файлы и
            // привязки принадлежат уничтожаемым сущностям — чистим их, иначе новые FK на document_facets
            // не пройдут по осиротевшим строкам generated_files. Блобы в MinIO подчистятся штатной
            // пересборкой; для dev-пересоздания это несущественно.
            migrationBuilder.Sql("DELETE FROM generated_files;");
            migrationBuilder.Sql("DELETE FROM dataset_bindings;");
            migrationBuilder.Sql("DELETE FROM document_set_outputs;");

            migrationBuilder.DropForeignKey(
                name: "FK_generated_files_document_instances_DocumentInstanceId",
                table: "generated_files");

            migrationBuilder.DropTable(
                name: "common_data_entries");

            migrationBuilder.DropTable(
                name: "document_instances");

            migrationBuilder.DropIndex(
                name: "IX_dataset_bindings_CommonDataEntryId",
                table: "dataset_bindings");

            migrationBuilder.DropIndex(
                name: "IX_dataset_bindings_InstanceId",
                table: "dataset_bindings");

            migrationBuilder.DropColumn(
                name: "CommonDataEntryId",
                table: "dataset_bindings");

            migrationBuilder.DropColumn(
                name: "InstanceId",
                table: "dataset_bindings");

            migrationBuilder.RenameColumn(
                name: "DocumentInstanceId",
                table: "generated_files",
                newName: "ObjectId");

            migrationBuilder.RenameIndex(
                name: "IX_generated_files_DocumentInstanceId",
                table: "generated_files",
                newName: "IX_generated_files_ObjectId");

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerId",
                table: "dataset_bindings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "domain_objects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Aliases = table.Column<List<string>>(type: "text[]", nullable: false),
                    CompositeTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Data = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    ScopeLevel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domain_objects", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "document_facets",
                columns: table => new
                {
                    ObjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    TemplateIds = table.Column<string>(type: "jsonb", nullable: true),
                    TemplateParams = table.Column<string>(type: "jsonb", nullable: true),
                    PluginData = table.Column<JsonDocument>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_facets", x => x.ObjectId);
                    table.ForeignKey(
                        name: "FK_document_facets_domain_objects_ObjectId",
                        column: x => x.ObjectId,
                        principalTable: "domain_objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dataset_bindings_OwnerId",
                table: "dataset_bindings",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_domain_objects_CompositeTypeId",
                table: "domain_objects",
                column: "CompositeTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_domain_objects_ScopeLevel_ScopeId",
                table: "domain_objects",
                columns: new[] { "ScopeLevel", "ScopeId" });

            migrationBuilder.AddForeignKey(
                name: "FK_generated_files_document_facets_ObjectId",
                table: "generated_files",
                column: "ObjectId",
                principalTable: "document_facets",
                principalColumn: "ObjectId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_generated_files_document_facets_ObjectId",
                table: "generated_files");

            migrationBuilder.DropTable(
                name: "document_facets");

            migrationBuilder.DropTable(
                name: "domain_objects");

            migrationBuilder.DropIndex(
                name: "IX_dataset_bindings_OwnerId",
                table: "dataset_bindings");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "dataset_bindings");

            migrationBuilder.RenameColumn(
                name: "ObjectId",
                table: "generated_files",
                newName: "DocumentInstanceId");

            migrationBuilder.RenameIndex(
                name: "IX_generated_files_ObjectId",
                table: "generated_files",
                newName: "IX_generated_files_DocumentInstanceId");

            migrationBuilder.AddColumn<Guid>(
                name: "CommonDataEntryId",
                table: "dataset_bindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InstanceId",
                table: "dataset_bindings",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "common_data_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Aliases = table.Column<List<string>>(type: "text[]", nullable: false),
                    CompositeTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Data = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_common_data_entries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "document_instances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DocumentSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    PluginData = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    Requisites = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    TemplateIds = table.Column<string>(type: "jsonb", nullable: true),
                    TemplateParams = table.Column<string>(type: "jsonb", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_instances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_document_instances_document_sets_DocumentSetId",
                        column: x => x.DocumentSetId,
                        principalTable: "document_sets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_dataset_bindings_CommonDataEntryId",
                table: "dataset_bindings",
                column: "CommonDataEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_dataset_bindings_InstanceId",
                table: "dataset_bindings",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_common_data_entries_CompositeTypeId",
                table: "common_data_entries",
                column: "CompositeTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_common_data_entries_Scope_ScopeId",
                table: "common_data_entries",
                columns: new[] { "Scope", "ScopeId" });

            migrationBuilder.CreateIndex(
                name: "IX_document_instances_DocumentSetId",
                table: "document_instances",
                column: "DocumentSetId");

            migrationBuilder.AddForeignKey(
                name: "FK_generated_files_document_instances_DocumentInstanceId",
                table: "generated_files",
                column: "DocumentInstanceId",
                principalTable: "document_instances",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
