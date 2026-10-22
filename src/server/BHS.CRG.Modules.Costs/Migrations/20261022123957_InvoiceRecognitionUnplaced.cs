using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceRecognitionUnplaced : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<JsonDocument>(
                name: "unplaced",
                schema: "costs",
                table: "invoice_recognitions",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "unplaced",
                schema: "costs",
                table: "invoice_recognitions");
        }
    }
}
