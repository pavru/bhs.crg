using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class InvoiceRequestLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "purchase_request_id",
                schema: "costs",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_invoices_purchase_request_id",
                schema: "costs",
                table: "invoices",
                column: "purchase_request_id");

            migrationBuilder.AddForeignKey(
                name: "FK_invoices_purchase_requests_purchase_request_id",
                schema: "costs",
                table: "invoices",
                column: "purchase_request_id",
                principalSchema: "costs",
                principalTable: "purchase_requests",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_invoices_purchase_requests_purchase_request_id",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "ix_invoices_purchase_request_id",
                schema: "costs",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "purchase_request_id",
                schema: "costs",
                table: "invoices");
        }
    }
}
