using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Plan.Migrations
{
    /// <inheritdoc />
    public partial class Calendars : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendar_years",
                schema: "plan",
                columns: table => new
                {
                    year = table.Column<int>(type: "integer", nullable: false),
                    working_week = table.Column<int>(type: "integer", nullable: false),
                    hours_per_shift = table.Column<decimal>(type: "numeric(4,2)", precision: 4, scale: 2, nullable: false),
                    import_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_years", x => x.year);
                });

            migrationBuilder.CreateTable(
                name: "construction_calendars",
                schema: "plan",
                columns: table => new
                {
                    construction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    working_week = table.Column<int>(type: "integer", nullable: true),
                    shifts = table.Column<int>(type: "integer", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_construction_calendars", x => x.construction_id);
                });

            migrationBuilder.CreateTable(
                name: "calendar_days",
                schema: "plan",
                columns: table => new
                {
                    year = table.Column<int>(type: "integer", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_calendar_days", x => new { x.year, x.date });
                    table.ForeignKey(
                        name: "FK_calendar_days_calendar_years_year",
                        column: x => x.year,
                        principalSchema: "plan",
                        principalTable: "calendar_years",
                        principalColumn: "year",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "construction_calendar_days",
                schema: "plan",
                columns: table => new
                {
                    construction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_construction_calendar_days", x => new { x.construction_id, x.date });
                    table.ForeignKey(
                        name: "FK_construction_calendar_days_construction_calendars_construct~",
                        column: x => x.construction_id,
                        principalSchema: "plan",
                        principalTable: "construction_calendars",
                        principalColumn: "construction_id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_days",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "construction_calendar_days",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "calendar_years",
                schema: "plan");

            migrationBuilder.DropTable(
                name: "construction_calendars",
                schema: "plan");
        }
    }
}
