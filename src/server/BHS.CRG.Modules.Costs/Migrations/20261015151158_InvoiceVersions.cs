using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invoice_versions",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "jsonb", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_invoice_versions_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "costs",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_invoice_versions_number",
                schema: "costs",
                table: "invoice_versions",
                columns: new[] { "invoice_id", "number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoice_versions",
                schema: "costs");
        }
    }
}
