using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BHS.CRG.Modules.Costs.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "purchase_requests",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    construction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_requests", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "purchase_request_lines",
                schema: "costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    nomenclature_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    quantity = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    unit = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchase_request_lines", x => x.id);
                    table.CheckConstraint("ck_purchase_request_lines_quantity", "quantity >= 0");
                    table.ForeignKey(
                        name: "FK_purchase_request_lines_purchase_requests_request_id",
                        column: x => x.request_id,
                        principalSchema: "costs",
                        principalTable: "purchase_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_request_lines_nomenclature",
                schema: "costs",
                table: "purchase_request_lines",
                column: "nomenclature_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_request_lines_order",
                schema: "costs",
                table: "purchase_request_lines",
                columns: new[] { "request_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_purchase_request_lines_plan_item",
                schema: "costs",
                table: "purchase_request_lines",
                column: "plan_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchase_requests_site_number",
                schema: "costs",
                table: "purchase_requests",
                columns: new[] { "construction_id", "number" });

            migrationBuilder.CreateIndex(
                name: "ux_purchase_requests_number",
                schema: "costs",
                table: "purchase_requests",
                column: "number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "purchase_request_lines",
                schema: "costs");

            migrationBuilder.DropTable(
                name: "purchase_requests",
                schema: "costs");
        }
    }
}
