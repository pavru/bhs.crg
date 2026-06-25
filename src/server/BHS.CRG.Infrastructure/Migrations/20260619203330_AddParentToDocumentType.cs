using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddParentToDocumentType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "document_types",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_types_ParentId",
                table: "document_types",
                column: "ParentId");

            migrationBuilder.AddForeignKey(
                name: "FK_document_types_document_types_ParentId",
                table: "document_types",
                column: "ParentId",
                principalTable: "document_types",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_document_types_document_types_ParentId",
                table: "document_types");

            migrationBuilder.DropIndex(
                name: "IX_document_types_ParentId",
                table: "document_types");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "document_types");
        }
    }
}
