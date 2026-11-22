using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class PriceList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "price_list_date",
                schema: "plan",
                table: "offers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "price_list_price",
                schema: "plan",
                table: "offer_lines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "price_list_valid_from",
                schema: "plan",
                table: "offer_lines",
                type: "date",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "price_list_entries",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    work_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    changed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_list_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_price_list_entries_work_type_date",
                schema: "plan",
                table: "price_list_entries",
                columns: new[] { "work_type_id", "valid_from" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "price_list_entries",
                schema: "plan");

            migrationBuilder.DropColumn(
                name: "price_list_date",
                schema: "plan",
                table: "offers");

            migrationBuilder.DropColumn(
                name: "price_list_price",
                schema: "plan",
                table: "offer_lines");

            migrationBuilder.DropColumn(
                name: "price_list_valid_from",
                schema: "plan",
                table: "offer_lines");
        }
    }
}
