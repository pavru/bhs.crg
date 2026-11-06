using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceUpdatedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "updated_by",
                schema: "costs",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "updated_by_name",
                schema: "costs",
                table: "invoices",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "updated_via",
                schema: "costs",
                table: "invoices",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "updated_by",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "updated_by_name",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "updated_via",
                schema: "costs",
                table: "invoices");
        }
    }
}
