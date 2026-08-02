using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class QualityAuditRun : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "quality_audit_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Documents = table.Column<int>(type: "integer", nullable: false),
                    Failed = table.Column<int>(type: "integer", nullable: false),
                    MaterialsWithoutDoc = table.Column<int>(type: "integer", nullable: false),
                    ImplausibleDocs = table.Column<int>(type: "integer", nullable: false),
                    TotalFindings = table.Column<int>(type: "integer", nullable: false),
                    RowsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quality_audit_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_quality_audit_runs_document_sets_SetId",
                        column: x => x.SetId,
                        principalTable: "document_sets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_quality_audit_runs_SetId",
                table: "quality_audit_runs",
                column: "SetId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "quality_audit_runs");
        }
    }
}
