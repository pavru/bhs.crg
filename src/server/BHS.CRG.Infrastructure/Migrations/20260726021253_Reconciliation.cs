using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Reconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "reconciliations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Spec = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliation_decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reconciliation_decisions_reconciliations_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "reconciliations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    MatchCount = table.Column<int>(type: "integer", nullable: false),
                    MismatchCount = table.Column<int>(type: "integer", nullable: false),
                    MissingLeftCount = table.Column<int>(type: "integer", nullable: false),
                    MissingRightCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliation_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reconciliation_runs_reconciliations_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "reconciliations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_findings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Label = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    LeftValue = table.Column<double>(type: "double precision", nullable: true),
                    RightValue = table.Column<double>(type: "double precision", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Provenance = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliation_findings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reconciliation_findings_reconciliation_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "reconciliation_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_reconciliation_decisions_DefinitionId_Key",
                table: "reconciliation_decisions",
                columns: new[] { "DefinitionId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reconciliation_findings_RunId_Key",
                table: "reconciliation_findings",
                columns: new[] { "RunId", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_reconciliation_runs_DefinitionId_StartedAt",
                table: "reconciliation_runs",
                columns: new[] { "DefinitionId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_reconciliations_Scope_ScopeId",
                table: "reconciliations",
                columns: new[] { "Scope", "ScopeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reconciliation_decisions");

            migrationBuilder.DropTable(
                name: "reconciliation_findings");

            migrationBuilder.DropTable(
                name: "reconciliation_runs");

            migrationBuilder.DropTable(
                name: "reconciliations");
        }
    }
}
