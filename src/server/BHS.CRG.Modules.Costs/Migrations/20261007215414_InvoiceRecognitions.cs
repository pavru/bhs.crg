using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceRecognitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invoice_recognitions",
                schema: "costs",
                columns: table => new
                {
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scan_blob_path = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    engine = table.Column<string>(type: "text", nullable: true),
                    values = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    offers = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    lines = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    notes = table.Column<string[]>(type: "text[]", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_recognitions", x => x.invoice_id);
                    table.ForeignKey(
                        name: "FK_invoice_recognitions_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "costs",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_recognitions_outcome",
                schema: "costs",
                table: "invoice_recognitions",
                column: "outcome");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoice_recognitions",
                schema: "costs");
        }
    }
}
