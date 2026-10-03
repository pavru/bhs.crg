using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class Invoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "costs");

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: true),
                    supplier_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purpose = table.Column<string>(type: "text", nullable: true),
                    total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    vat_total = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    shipped_on = table.Column<DateOnly>(type: "date", nullable: true),
                    deferral_days = table.Column<int>(type: "integer", nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    due_date_manual = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    payment = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scan_blob_path = table.Column<string>(type: "text", nullable: true),
                    scan_file_name = table.Column<string>(type: "text", nullable: true),
                    scan_mime_type = table.Column<string>(type: "text", nullable: true),
                    unconfirmed = table.Column<string[]>(type: "text[]", nullable: false),
                    data = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_due",
                schema: "costs",
                table: "invoices",
                columns: new[] { "payment", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_duplicate",
                schema: "costs",
                table: "invoices",
                columns: new[] { "supplier_id", "number", "issued_on" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoices",
                schema: "costs");
        }
    }
}
