using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "schedule_links",
                schema: "plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    predecessor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    successor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    lag = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_links", x => x.id);
                    table.ForeignKey(
                        name: "FK_schedule_links_schedule_works_predecessor_id",
                        column: x => x.predecessor_id,
                        principalSchema: "plan",
                        principalTable: "schedule_works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_schedule_links_schedule_works_successor_id",
                        column: x => x.successor_id,
                        principalSchema: "plan",
                        principalTable: "schedule_works",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_schedule_links_predecessor_id_successor_id",
                schema: "plan",
                table: "schedule_links",
                columns: new[] { "predecessor_id", "successor_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_schedule_links_successor_id",
                schema: "plan",
                table: "schedule_links",
                column: "successor_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schedule_links",
                schema: "plan");
        }
    }
}
